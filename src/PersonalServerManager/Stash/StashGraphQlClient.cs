using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalServerManager.Stash;

public sealed class StashGraphQlClient(HttpClient httpClient) : IStashGraphQlClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public async Task<string?> GetMarkerSceneIdAsync(StashInstance instance, string markerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!long.TryParse(markerId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            throw new ArgumentException("Invalid marker ID.", nameof(markerId));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await SendAsync(instance,
            "query MarkerScene($id: ID!) { findSceneMarkers(ids: [$id], filter: { per_page: 1, page: 1 }) { scene_markers { id scene { id } } } }",
            new { id = numericId.ToString(CultureInfo.InvariantCulture) }, timeout.Token);
        await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
        using var payload = await ReadPayloadAsync(response, timeout.Token);
        ThrowForGraphQlErrors(payload.RootElement);
        if (!payload.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("findSceneMarkers", out var result) ||
            result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("scene_markers", out var markers) ||
            markers.ValueKind != JsonValueKind.Array || markers.GetArrayLength() > 1)
            throw new InvalidDataException("Stash returned no marker data.");
        if (markers.GetArrayLength() == 0) return null;
        var marker = markers[0];
        if (marker.ValueKind != JsonValueKind.Object || !marker.TryGetProperty("id", out var returnedId) ||
            returnedId.ValueKind != JsonValueKind.String || returnedId.GetString() != numericId.ToString(CultureInfo.InvariantCulture) ||
            !marker.TryGetProperty("scene", out var scene) ||
            scene.ValueKind != JsonValueKind.Object || !scene.TryGetProperty("id", out var sceneId) ||
            sceneId.ValueKind != JsonValueKind.String ||
            !long.TryParse(sceneId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var numericSceneId) || numericSceneId <= 0)
            throw new InvalidDataException("Stash returned an invalid marker scene ID.");
        return numericSceneId.ToString(CultureInfo.InvariantCulture);
    }

    public Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        CancellationToken cancellationToken = default) => SearchAsync(instance, kind, query, 5, cancellationToken);

    public async Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (field, collectionName, selection, urlSegment) = kind switch
        {
            StashSearchKind.Scenes => ("findScenes", "scenes", "id title paths { screenshot }", "scenes"),
            StashSearchKind.Images => ("findImages", "images", "id title paths { thumbnail }", "images"),
            StashSearchKind.Performers => ("findPerformers", "performers", "id name image_path", "performers"),
            StashSearchKind.Studios => ("findStudios", "studios", "id name image_path", "studios"),
            StashSearchKind.Tags => ("findTags", "tags", "id name image_path", "tags"),
            StashSearchKind.Markers => ("findSceneMarkers", "scene_markers", "id title seconds scene { id } primary_tag { name } screenshot", "scenes"),
            StashSearchKind.Galleries => ("findGalleries", "galleries", "id title paths { cover }", "galleries"),
            StashSearchKind.Groups => ("findGroups", "groups", "id name front_image_path", "groups"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var document = $"query Search($filter: FindFilterType) {{ {field}(filter: $filter) {{ count {collectionName} {{ {selection} }} }} }}";
        using var response = await SendAsync(
            instance,
            document,
            new { filter = new { q = query, per_page = limit, page = 1 } },
            cancellationToken);

        using var payload = await ReadPayloadAsync(response, cancellationToken);
        ThrowForGraphQlErrors(payload.RootElement);

        if (!payload.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(field, out var collection))
        {
            throw new InvalidDataException($"Stash returned no {kind.ToString().ToLowerInvariant()} search data.");
        }

        var items = collection.TryGetProperty(collectionName, out var results)
            ? results
            : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Stash returned an invalid search result list.");
        }

        return items.EnumerateArray()
            .Take(limit)
            .Select(item => MapResult(instance, kind, urlSegment, item))
            .ToArray();
    }

    public async Task<StashObservation> ObserveAsync(
        StashInstance instance,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var response = await SendAsync(
                instance,
                "query Observe { findScenes(filter: { per_page: 1, page: 1 }) { count scenes { id } } }",
                null,
                cancellationToken);
            using var payload = await ReadPayloadAsync(response, cancellationToken);
            ThrowForGraphQlErrors(payload.RootElement);

            var count = payload.RootElement.GetProperty("data")
                .GetProperty("findScenes")
                .GetProperty("count")
                .GetInt32();

            return new StashObservation(instance.Id, instance.Name, true, checkedAt,
                $"GraphQL responded; {count} scenes indexed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or KeyNotFoundException)
        {
            return new StashObservation(instance.Id, instance.Name, false, checkedAt, SafeMessage(exception));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        StashInstance instance,
        string document,
        object? variables,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, instance.GraphQlEndpoint)
        {
            Content = JsonContent.Create(new { query = document, variables })
        };
        if (!string.IsNullOrWhiteSpace(instance.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.ApiKey);
        }

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"Stash returned HTTP {(int)response.StatusCode}.");
        }

        return response;
    }

    private static async Task<JsonDocument> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void ThrowForGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return;
        }

        var message = errors[0].TryGetProperty("message", out var value)
            ? value.GetString()
            : null;
        throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
            ? "Stash reported a GraphQL error."
            : $"Stash GraphQL error: {message}");
    }

    private static StashSearchResult MapResult(StashInstance instance, StashSearchKind kind, string urlSegment, JsonElement item)
    {
        var id = item.GetProperty("id").GetString() ?? throw new InvalidDataException("Stash returned a result without an ID.");
        var named = kind is StashSearchKind.Performers or StashSearchKind.Studios or StashSearchKind.Tags or StashSearchKind.Groups;
        var title = item.TryGetProperty(named ? "name" : "title", out var titleValue)
            ? titleValue.GetString() ?? "(untitled)"
            : "(untitled)";
        var thumbnail = kind switch
        {
            StashSearchKind.Performers or StashSearchKind.Studios or StashSearchKind.Tags => item.TryGetProperty("image_path", out var image) ? image.GetString() : null,
            StashSearchKind.Scenes => GetNestedString(item, "paths", "screenshot"),
            StashSearchKind.Images => GetNestedString(item, "paths", "thumbnail"),
            StashSearchKind.Galleries => GetNestedString(item, "paths", "cover"),
            StashSearchKind.Groups => item.TryGetProperty("front_image_path", out var front) ? front.GetString() : null,
            StashSearchKind.Markers => item.TryGetProperty("screenshot", out var screenshot) ? screenshot.GetString() : null,
            _ => null
        };

        var itemPath = $"{urlSegment}/{Uri.EscapeDataString(id)}";
        if (kind == StashSearchKind.Markers)
        {
            var sceneId = GetNestedString(item, "scene", "id");
            if (string.IsNullOrWhiteSpace(sceneId) || !item.TryGetProperty("seconds", out var secondsValue) ||
                secondsValue.ValueKind != JsonValueKind.Number || !secondsValue.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0)
            {
                throw new InvalidDataException("Stash returned invalid marker navigation data.");
            }

            itemPath = $"scenes/{Uri.EscapeDataString(sceneId)}?t={seconds.ToString("R", CultureInfo.InvariantCulture)}";
            if (string.IsNullOrWhiteSpace(title))
                title = GetNestedString(item, "primary_tag", "name") ?? "(untitled)";
        }

        return new StashSearchResult(
            id,
            title,
            kind,
            ResolveThumbnail(instance.WebUiUrl, kind, id, thumbnail, kind == StashSearchKind.Markers ? GetNestedString(item, "scene", "id") : null),
            new Uri(instance.WebUiUrl, itemPath),
            instance.Id,
            instance.Name);
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName) =>
        element.TryGetProperty(objectName, out var nested) && nested.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;

    private static Uri? ResolveThumbnail(Uri baseUri, StashSearchKind kind, string id, string? value, string? sceneId = null)
    {
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            return null;

        var path = kind switch
        {
            StashSearchKind.Scenes => $"scene/{id}/screenshot",
            StashSearchKind.Images => $"image/{id}/thumbnail",
            StashSearchKind.Performers => $"performer/{id}/image",
            StashSearchKind.Studios => $"studio/{id}/image",
            StashSearchKind.Tags => $"tag/{id}/image",
            StashSearchKind.Galleries => $"gallery/{id}/cover",
            StashSearchKind.Groups => $"group/{id}/frontimage",
            StashSearchKind.Markers when long.TryParse(sceneId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSceneId) && numericSceneId > 0
                => $"scene/{numericSceneId.ToString(CultureInfo.InvariantCulture)}/scene_marker/{id}/screenshot",
            _ => null
        };
        if (path is null || !Uri.TryCreate(baseUri, value, out var candidate))
            return null;

        var expected = new Uri(baseUri, path);
        return candidate.Scheme == expected.Scheme && candidate.Authority == expected.Authority &&
            candidate.UserInfo.Length == 0 && candidate.AbsolutePath == expected.AbsolutePath
            ? expected : null;
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The request timed out.",
        HttpRequestException => exception.Message,
        InvalidDataException => exception.Message,
        _ => "Stash returned an invalid response."
    };
}