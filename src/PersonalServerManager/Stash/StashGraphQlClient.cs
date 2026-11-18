using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalServerManager.Stash;

public sealed class StashGraphQlClient(HttpClient httpClient) : IStashGraphQlClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        CancellationToken cancellationToken = default)
    {
        var (field, selection, urlSegment) = kind switch
        {
            StashSearchKind.Scenes => ("findScenes", "scenes { id title paths { screenshot } }", "scenes"),
            StashSearchKind.Images => ("findImages", "images { id title paths { thumbnail } }", "images"),
            StashSearchKind.Performers => ("findPerformers", "performers { id name image_path }", "performers"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var document = $"query Search($filter: FindFilterType) {{ {field}(filter: $filter) {{ count {selection} }} }}";
        using var response = await SendAsync(
            instance,
            document,
            new { filter = new { q = query, per_page = 5, page = 1 } },
            cancellationToken);

        using var payload = await ReadPayloadAsync(response, cancellationToken);
        ThrowForGraphQlErrors(payload.RootElement);

        if (!payload.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(field, out var collection))
        {
            throw new InvalidDataException($"Stash returned no {kind.ToString().ToLowerInvariant()} search data.");
        }

        var items = collection.TryGetProperty(field switch
        {
            "findScenes" => "scenes",
            "findImages" => "images",
            _ => "performers"
        }, out var results)
            ? results
            : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Stash returned an invalid search result list.");
        }

        return items.EnumerateArray()
            .Take(5)
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
        var title = item.TryGetProperty(kind == StashSearchKind.Performers ? "name" : "title", out var titleValue)
            ? titleValue.GetString() ?? "(untitled)"
            : "(untitled)";
        var thumbnail = kind switch
        {
            StashSearchKind.Performers => item.TryGetProperty("image_path", out var image) ? image.GetString() : null,
            StashSearchKind.Scenes => GetNestedString(item, "paths", "screenshot"),
            _ => GetNestedString(item, "paths", "thumbnail")
        };

        return new StashSearchResult(
            id,
            title,
            kind,
            ResolveInstanceUrl(instance.WebUiUrl, thumbnail),
            new Uri(instance.WebUiUrl, $"{urlSegment}/{Uri.EscapeDataString(id)}"),
            instance.Id,
            instance.Name);
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName) =>
        element.TryGetProperty(objectName, out var nested) && nested.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;

    private static Uri? ResolveInstanceUrl(Uri baseUri, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new Uri(baseUri, value);

    private static string SafeMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The request timed out.",
        HttpRequestException => exception.Message,
        InvalidDataException => exception.Message,
        _ => "Stash returned an invalid response."
    };
}