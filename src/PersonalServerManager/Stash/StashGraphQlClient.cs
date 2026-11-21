using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PersonalServerManager.Feeds;
using PersonalServerManager.Search;

namespace PersonalServerManager.Stash;

public sealed class StashGraphQlClient(HttpClient httpClient) : IStashGraphQlClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<StashSearchResult>> SearchFeedAsync(
        StashInstance instance, StashSearchKind kind, FeedSource source, FeedOrder order,
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(source);
        if (kind is not (StashSearchKind.Scenes or StashSearchKind.Images)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var (sort, direction) = order switch
        {
            FeedOrder.Newest => ("created_at", "DESC"),
            FeedOrder.Oldest => ("created_at", "ASC"),
            FeedOrder.Random => ($"random_{BitConverter.ToUInt32(SHA256.HashData(instance.Id.ToByteArray()), 0).ToString(CultureInfo.InvariantCulture)}", "ASC"),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        if (source.Media is not (FeedMedia.All or FeedMedia.Images or FeedMedia.Videos)) throw new ArgumentOutOfRangeException(nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        if ((kind == StashSearchKind.Scenes && source.Media == FeedMedia.Images) ||
            (kind == StashSearchKind.Images && source.Media == FeedMedia.Videos)) return [];

        var objectFilter = new Dictionary<string, object>();
        foreach (var (includedNames, excludedNames, entity, property) in new[]
        {
            (source.IncludeTags, source.ExcludeTags, "tag", "tags"),
            (source.IncludePerformers, source.ExcludePerformers, "performer", "performers")
        })
        {
            var includes = includedNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            var excludes = excludedNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            var includeIds = await ResolveFeedNamesAsync(instance, entity, includes, cancellationToken);
            if (includes.Length > 0 && includeIds.Count == 0) return [];
            var excludeIds = await ResolveFeedNamesAsync(instance, entity, excludes, cancellationToken);
            if (includeIds.Count == 0 && excludeIds.Count == 0) continue;
            var criterion = new Dictionary<string, object> { ["modifier"] = "INCLUDES" };
            if (includeIds.Count > 0) criterion["value"] = includeIds;
            if (excludeIds.Count > 0) criterion["excludes"] = excludeIds;
            if (entity == "tag") criterion["depth"] = 0;
            objectFilter[property] = criterion;
        }

        var (field, collection, argument, filterType, selection) = kind == StashSearchKind.Scenes
            ? ("findScenes", "scenes", "scene_filter", "SceneFilterType", "id title paths { screenshot webp } files { path duration } rating100")
            : ("findImages", "images", "image_filter", "ImageFilterType", "id title paths { thumbnail preview } files { path } visual_files { ... on ImageFile { path } ... on VideoFile { path } } rating100");
        var document = $"query Feed($filter: FindFilterType, $criteria: {filterType}) {{ {field}(filter: $filter, {argument}: $criteria) {{ {collection} {{ {selection} created_at updated_at }} }} }}";
        var results = new List<StashSearchResult>();
        var skip = offset % limit;
        var page = (long)offset / limit + 1;
        while (results.Count < limit)
        {
            if (page > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(offset));
            var filter = new { per_page = limit, page = (int)page, sort, direction };
            using var response = await SendAsync(instance, document, new { filter, criteria = objectFilter }, cancellationToken);
            using var payload = await ReadPayloadAsync(response, cancellationToken);
            ThrowForGraphQlErrors(payload.RootElement);
            if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty(field, out var group) || group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Stash returned an invalid feed result list.");
            var count = items.GetArrayLength();
            results.AddRange(items.EnumerateArray().Skip(skip).Take(limit - results.Count).Select(item => MapResult(instance, kind, collection, item)));
            if (count < limit) break;
            skip = 0;
            page++;
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> ResolveFeedNamesAsync(
        StashInstance instance, string entity, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var field = entity == "tag" ? "findTags" : "findPerformers";
        var collection = entity == "tag" ? "tags" : "performers";
        var type = entity == "tag" ? "TagFilterType" : "PerformerFilterType";
        var document = $"query FeedNames($criteria: {type}) {{ {field}({entity}_filter: $criteria, filter: {{ per_page: -1 }}) {{ {collection} {{ id name }} }} }}";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var response = await SendAsync(instance, document, new { criteria = new { name = new { value = name, modifier = "EQUALS" } } }, cancellationToken);
            using var payload = await ReadPayloadAsync(response, cancellationToken);
            ThrowForGraphQlErrors(payload.RootElement);
            if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty(field, out var group) || group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Stash returned an invalid feed name lookup.");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var itemName) || itemName.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw new InvalidDataException("Stash returned an invalid feed name lookup.");
                if (string.Equals(name, itemName.GetString(), StringComparison.Ordinal)) ids.Add(id.GetString()!);
            }
        }
        return ids.ToArray();
    }

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

    public Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        int limit,
        CancellationToken cancellationToken = default) => SearchAsync(instance, kind, query, limit, LibrarySort.Default, cancellationToken);

    public async Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        int limit,
        LibrarySort sort,
        CancellationToken cancellationToken = default)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (field, collectionName, selection, urlSegment) = kind switch
        {
            StashSearchKind.Scenes => ("findScenes", "scenes", "id title paths { screenshot webp } files { path duration } rating100", "scenes"),
            StashSearchKind.Images => ("findImages", "images", "id title paths { thumbnail preview } files { path } visual_files { ... on ImageFile { path } ... on VideoFile { path } } rating100", "images"),
            StashSearchKind.Performers => ("findPerformers", "performers", "id name image_path rating100 favorite", "performers"),
            StashSearchKind.Studios => ("findStudios", "studios", "id name image_path rating100 favorite", "studios"),
            StashSearchKind.Tags => ("findTags", "tags", "id name image_path favorite", "tags"),
            StashSearchKind.Markers => ("findSceneMarkers", "scene_markers", "id title seconds end_seconds scene { id } primary_tag { name } screenshot", "scenes"),
            StashSearchKind.Galleries => ("findGalleries", "galleries", "id title paths { cover } files { path } rating100", "galleries"),
            StashSearchKind.Groups => ("findGroups", "groups", "id name front_image_path scenes { id } rating100", "groups"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var document = $"query Search($filter: FindFilterType) {{ {field}(filter: $filter) {{ count {collectionName} {{ {selection} created_at updated_at }} }} }}";
        var filter = new Dictionary<string, object> { ["q"] = query, ["per_page"] = limit, ["page"] = 1 };
        var named = kind is StashSearchKind.Performers or StashSearchKind.Studios or StashSearchKind.Tags or StashSearchKind.Groups;
        (string Field, string Direction)? order = sort switch
        {
            LibrarySort.TitleAscending => (named ? "name" : "title", "ASC"),
            LibrarySort.TitleDescending => (named ? "name" : "title", "DESC"),
            LibrarySort.DateNewest => ("created_at", "DESC"),
            LibrarySort.DateOldest => ("created_at", "ASC"),
            LibrarySort.Random => ($"random_{Random.Shared.Next(1, 100_000_000).ToString(CultureInfo.InvariantCulture)}", "ASC"),
            LibrarySort.Default => null,
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        if (order is { } value)
        {
            filter["sort"] = value.Field;
            filter["direction"] = value.Direction;
        }

        using var response = await SendAsync(
            instance,
            document,
            new { filter },
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
            throw new HttpRequestException($"Stash returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
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
        var path = ReadFilePath(item, kind == StashSearchKind.Images ? "visual_files" : "files") ?? ReadFilePath(item, "files");
        var title = item.TryGetProperty(named ? "name" : "title", out var titleValue)
            && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) title = kind == StashSearchKind.Markers ? "" : path ?? "(untitled)";
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
            instance.Name)
        {
            CreatedAt = ReadDate(item, "created_at"),
            UpdatedAt = ReadDate(item, "updated_at"),
            Duration = kind == StashSearchKind.Scenes ? ReadFileDuration(item) : null,
            MarkerStart = kind == StashSearchKind.Markers ? ReadMarkerTime(item, "seconds") : null,
            MarkerEnd = kind == StashSearchKind.Markers ? ReadMarkerTime(item, "end_seconds") : null,
            HasAnimatedPreview = kind is StashSearchKind.Scenes or StashSearchKind.Images &&
                ResolveThumbnail(instance.WebUiUrl, kind, id, GetNestedString(item, "paths", kind == StashSearchKind.Scenes ? "webp" : "preview"), animated: true) is not null,
            Path = path,
            ContentCount = kind switch
            {
                StashSearchKind.Galleries => ReadArrayCount(item, "files"),
                StashSearchKind.Groups => ReadArrayCount(item, "scenes"),
                _ => null
            },
            Rating100 = item.TryGetProperty("rating100", out var rating) && rating.ValueKind == JsonValueKind.Number &&
                rating.TryGetInt32(out var score) && score is > 0 and <= 100 ? score : null,
            IsFavorite = item.TryGetProperty("favorite", out var favorite) && favorite.ValueKind == JsonValueKind.True
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;

    private static string? ReadFilePath(JsonElement item, string property) =>
        item.TryGetProperty(property, out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Where(file => file.ValueKind == JsonValueKind.Object)
                .Select(file => file.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static TimeSpan? ReadDuration(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0 ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;

        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan? ReadMarkerTime(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0 ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;

        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan? ReadFileDuration(JsonElement item)
    {
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var file in files.EnumerateArray())
        {
            var duration = ReadDuration(file, "duration");
            if (duration.HasValue)
                return duration;
        }

        return null;
    }

    private static int? ReadArrayCount(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : null;

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName) =>
        element.TryGetProperty(objectName, out var nested) && nested.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;

    private static Uri? ResolveThumbnail(Uri baseUri, StashSearchKind kind, string id, string? value, string? sceneId = null, bool animated = false)
    {
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            return null;

        var path = kind switch
        {
            StashSearchKind.Scenes => $"scene/{id}/{(animated ? "webp" : "screenshot")}",
            StashSearchKind.Images => $"image/{id}/{(animated ? "preview" : "thumbnail")}",
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