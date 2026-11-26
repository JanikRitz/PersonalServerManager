using System.Text.Json;

namespace PersonalServerManager.RandomPlot;

public sealed record RandomPlotInstance(Guid Id, string Name, Uri BaseUrl, string? InstallationFolder = null,
    int Port = 5243, bool StartWithApp = false, string? DataFilePath = null)
{
    public bool IsManaged => !string.IsNullOrWhiteSpace(InstallationFolder);
    public string PathBase => $"/managed/randomplot/{Id:D}";
    public Uri ApiBaseUrl => IsManaged ? new Uri($"http://127.0.0.1:{Port}{PathBase}/") : BaseUrl;
    public Uri McpUrl => new(ApiBaseUrl, "mcp");
    public string BrowserUrl => IsManaged ? PathBase + "/" : BaseUrl.AbsoluteUri;
}

public sealed record RandomPlotValue(string Value);
public sealed record RandomPlotGroup(string Name, IReadOnlyList<RandomPlotValue> Values, bool IsFavorite);

public interface IRandomPlotInstanceStore
{
    Task<IReadOnlyList<RandomPlotInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RandomPlotInstance instance, CancellationToken cancellationToken = default);
    Task UpdateAsync(RandomPlotInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class JsonRandomPlotInstanceStore(string filePath) : IRandomPlotInstanceStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RandomPlotInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAsync(cancellationToken)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public Task SaveAsync(RandomPlotInstance instance, CancellationToken cancellationToken = default) => SaveCoreAsync(instance, false, cancellationToken);
    public Task UpdateAsync(RandomPlotInstance instance, CancellationToken cancellationToken = default) => SaveCoreAsync(instance, true, cancellationToken);

    private async Task SaveCoreAsync(RandomPlotInstance instance, bool requireExisting, CancellationToken cancellationToken)
    {
        instance = Normalize(instance);
        if (instance.IsManaged && (!Path.IsPathFullyQualified(instance.InstallationFolder!) ||
            !File.Exists(Path.Combine(instance.InstallationFolder!, "RandomPlotGenerator.exe"))))
            throw new InvalidDataException("The published folder must be absolute and contain RandomPlotGenerator.exe.");
        if (instance.DataFilePath is not null && !File.Exists(instance.DataFilePath))
            throw new InvalidDataException("The existing data file could not be found.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAsync(cancellationToken);
            var index = items.FindIndex(item => item.Id == instance.Id);
            if (requireExisting && index < 0) throw new InvalidDataException("The RandomPlotGenerator instance no longer exists.");
            if (instance.IsManaged && items.Any(item => item.Id != instance.Id && item.IsManaged && item.Port == instance.Port))
                throw new InvalidDataException("Each managed RandomPlotGenerator instance needs a different port.");
            if (instance.DataFilePath is not null && items.Any(item => item.Id != instance.Id && item.IsManaged &&
                string.Equals(item.DataFilePath, instance.DataFilePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                throw new InvalidDataException("Each managed RandomPlotGenerator instance needs a different data file.");
            if (index < 0) items.Add(instance); else items[index] = instance;
            await WriteAsync(items, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAsync(cancellationToken);
            if (items.RemoveAll(item => item.Id == id) > 0) await WriteAsync(items, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static RandomPlotInstance Normalize(RandomPlotInstance instance)
    {
        if (instance.Id == Guid.Empty || string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Trim().Length > 60)
            throw new InvalidDataException("Enter an instance name of up to 60 characters.");
        if (instance.Port is < 1 or > 65535 || instance.BaseUrl is null || !instance.BaseUrl.IsAbsoluteUri ||
            (instance.BaseUrl.Scheme != Uri.UriSchemeHttp && instance.BaseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(instance.BaseUrl.UserInfo) || !string.IsNullOrEmpty(instance.BaseUrl.Query) || !string.IsNullOrEmpty(instance.BaseUrl.Fragment))
            throw new InvalidDataException("Enter a valid HTTP(S) URL without credentials, query, or fragment and a valid port.");
        var folder = string.IsNullOrWhiteSpace(instance.InstallationFolder) ? null : instance.InstallationFolder.Trim();
        var dataFile = string.IsNullOrWhiteSpace(instance.DataFilePath) ? null : instance.DataFilePath.Trim();
        if (dataFile is not null && (folder is null || !Path.IsPathFullyQualified(dataFile)))
            throw new InvalidDataException("The existing data file must be an absolute path for a managed instance.");
        if (dataFile is not null) dataFile = Path.GetFullPath(dataFile);
        if (folder is not null && instance.BaseUrl != new Uri($"http://127.0.0.1:{instance.Port}/"))
            throw new InvalidDataException("Managed RandomPlotGenerator must use its loopback URL and configured port.");
        return instance with { Name = instance.Name.Trim(), BaseUrl = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/"),
            InstallationFolder = folder, StartWithApp = folder is not null && instance.StartWithApp, DataFilePath = dataFile };
    }

    private async Task<List<RandomPlotInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(filePath);
            var items = await JsonSerializer.DeserializeAsync<List<RandomPlotInstance>>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException();
            if (items.Any(item => item is null) || items.Select(item => item.Id).Distinct().Count() != items.Count)
                throw new InvalidDataException();
            return items.Select(Normalize).ToList();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException("The RandomPlotGenerator configuration file is invalid. It was left untouched.");
        }
    }

    private async Task WriteAsync(List<RandomPlotInstance> items, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, items, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}