using System.Globalization;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Stash;

namespace PersonalServerManager.Search;

public record LibraryItem(string Id, string Title, string Detail, Uri? ItemUrl, Uri? PreviewUrl = null);

public record LibraryGroup(
    Guid InstanceId,
    string InstanceName,
    string Provider,
    string Category,
    IReadOnlyList<LibraryItem> Items,
    Uri? SearchUrl,
    string? Error);

public sealed class LibrarySearchService(IStashGraphQlClient stashClient, IHydrusClient hydrusClient)
{
    public async Task<IReadOnlyList<LibraryGroup>> SearchAsync(
        IReadOnlyList<StashInstance> stashInstances,
        IReadOnlyList<HydrusInstance> hydrusInstances,
        string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = query.Trim();
        var tags = ParseHydrusTags(capturedQuery);
        using var semaphore = new SemaphoreSlim(6);
        var tasks = new List<Task<LibraryGroup>>();

        foreach (var instance in stashInstances)
        {
            foreach (var kind in Enum.GetValues<StashSearchKind>())
            {
                tasks.Add(SearchGroupAsync(instance.Id, instance.Name, "Stash", kind.ToString(),
                    new Uri(instance.WebUiUrl, $"{kind.ToString().ToLowerInvariant()}?q={Uri.EscapeDataString(capturedQuery)}"),
                    async () => (await stashClient.SearchAsync(instance, kind, capturedQuery, cancellationToken))
                        .Take(5)
                        .Select(item => new LibraryItem(item.Id, item.Title, kind.ToString(), item.ItemUrl,
                            PreviewService.CreateLink("stash", instance.Id, kind.ToString(), item.Id)))
                        .ToArray()));
            }
        }

        foreach (var instance in hydrusInstances)
        {
            tasks.Add(SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Files", null,
                async () => (await hydrusClient.SearchAsync(instance, tags, cancellationToken))
                    .Take(5)
                    .Select(item => new LibraryItem(item.Id.ToString(CultureInfo.InvariantCulture), item.Title, item.Mime, null,
                        PreviewService.CreateLink("hydrus", instance.Id, "files", item.Id.ToString(CultureInfo.InvariantCulture))))
                    .ToArray()));
        }

        var groups = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return groups;

        async Task<LibraryGroup> SearchGroupAsync(
            Guid instanceId,
            string instanceName,
            string provider,
            string category,
            Uri? searchUrl,
            Func<Task<IReadOnlyList<LibraryItem>>> search)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = await search();
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryGroup(instanceId, instanceName, provider, category, items, searchUrl, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryGroup(instanceId, instanceName, provider, category, [], searchUrl,
                    "Search failed. Check the instance connection and try again.");
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    public static IReadOnlyList<string> ParseHydrusTags(string query) =>
        Array.AsReadOnly(query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}