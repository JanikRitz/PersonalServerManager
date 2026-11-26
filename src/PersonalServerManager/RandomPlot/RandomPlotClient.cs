using System.Net.Http.Json;

namespace PersonalServerManager.RandomPlot;

public interface IRandomPlotClient
{
    Task<IReadOnlyList<RandomPlotGroup>> GetGroupsAsync(RandomPlotInstance instance, string query, CancellationToken cancellationToken = default);
}

public sealed class RandomPlotClient(HttpClient client) : IRandomPlotClient
{
    public async Task<IReadOnlyList<RandomPlotGroup>> GetGroupsAsync(RandomPlotInstance instance, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(query);
        var uri = new Uri(instance.ApiBaseUrl, "api/groups?query=" + Uri.EscapeDataString(query));
        return await client.GetFromJsonAsync<RandomPlotGroup[]>(uri, cancellationToken)
            ?? throw new InvalidDataException("RandomPlotGenerator returned an invalid group list.");
    }
}