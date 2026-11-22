using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using PersonalServerManager.Management;

namespace PersonalServerManager.Search;

public sealed class ComicCompanionClient(HttpClient client)
{
    internal async Task<IReadOnlyList<LibraryItem>> SearchAsync(ComicCompanionSettings settings,
        string query, int limit, LibrarySort sort, bool imagesets, CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString(new Uri(settings.ApiUrl!, "api/manager/comics").AbsoluteUri,
            new Dictionary<string, string?>
            {
                ["q"] = query,
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
                ["sort"] = sort.ToString(),
                ["kind"] = imagesets ? "Imageset" : "Comic"
            });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(256 * 1024, timeout.Token);
        var comics = await response.Content.ReadFromJsonAsync<ComicResult[]>(timeout.Token)
            ?? throw new InvalidDataException("Comic Companion returned an invalid catalog response.");
        return comics.Where(comic => comic is { Id: > 0 } && !string.IsNullOrWhiteSpace(comic.Title) &&
            (imagesets ? comic.Kind == "Imageset" : comic.Kind is null or "Comic"))
            .DistinctBy(comic => comic.Id)
            .Take(limit)
            .Select(comic =>
            {
                var id = comic.Id.ToString(CultureInfo.InvariantCulture);
                var browser = settings.EffectiveBrowserUrl!;
                var reader = GetReaderUrl(browser, id, imagesets, comic.ReaderPath);
                return new LibraryItem(id, comic.Title, imagesets ? "Hydrus imageset" : "Hydrus comic",
                    reader is null ? settings.BrowserLink(new Uri(browser, $"{(imagesets ? "collection" : "comic")}/{id}")) : null,
                    comic.CoverHash is null ? null : PreviewService.CreateLink("comiccompanion", settings.Id, "comics", comic.CoverHash),
                    imagesets ? LibraryMediaType.Collection : LibraryMediaType.Comic, UpdatedAt: comic.UpdatedAt, ReaderUrl: reader is null ? null : settings.BrowserLink(reader))
                {
                    ContentCount = imagesets ? comic.ImageCount : comic.PageCount
                };
            }).ToArray();
    }

    private static Uri? GetReaderUrl(Uri browser, string id, bool imageset, string? path)
    {
        if (path is null || path.Length > 128 || path.Split('/') is not [var route, var item, var section, var page] || item != id)
            return null;

        var valid = imageset
            ? route == "collection" && section == "image" && page.Length == 64 && page.All(Uri.IsHexDigit)
            : route == "read" && IsPositiveId(section) && IsPositiveId(page);
        return valid ? new Uri(browser, path) : null;
    }

    private static bool IsPositiveId(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    private sealed record ComicResult(int Id, string Title, string? CoverHash, DateTimeOffset? UpdatedAt, string? Kind,
        string? ReaderPath, int? PageCount, int? ImageCount);
}