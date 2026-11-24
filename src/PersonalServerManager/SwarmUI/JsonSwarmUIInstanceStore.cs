using System.Text.Json;
using PersonalServerManager.Management;

namespace PersonalServerManager.SwarmUI;

public sealed class JsonSwarmUIInstanceStore(string filePath) : ISwarmUIInstanceStore
{
    private const string InvalidFileMessage = "The SwarmUI configuration file is invalid. It was left untouched.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<SwarmUIInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            return stored.Select(ToInstance).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(instance, false, cancellationToken);

    public Task UpdateAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(instance, true, cancellationToken);

    private async Task SaveCoreAsync(SwarmUIInstance instance, bool requireExisting, CancellationToken cancellationToken)
    {
        var normalized = Normalize(instance);
        ManagedBackendService.ValidateSwarmUIInstance(normalized);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            var replacement = new StoredInstance(normalized.Id, normalized.Name, normalized.BaseUrl.AbsoluteUri,
                normalized.SearchPaths.Select(path => new StoredSearchPath(path.Path, path.Depth)).ToList(),
                normalized.InstallationFolder, normalized.Port, normalized.StartWithApp, normalized.ListenOnLocalNetwork);
            var existing = stored.FindIndex(item => item.Id == normalized.Id);
            if (requireExisting && existing < 0)
                throw new InvalidDataException("The SwarmUI instance no longer exists.");
            if (existing < 0)
            {
                stored.Add(replacement);
            }
            else
            {
                stored[existing] = replacement;
            }

            await WriteAsync(stored, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            if (stored.RemoveAll(item => item.Id == id) > 0)
            {
                await WriteAsync(stored, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SwarmUIInstance Normalize(SwarmUIInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Trim().Length > 60)
            throw new InvalidDataException("Enter a SwarmUI instance name of up to 60 characters.");
        try
        {
            SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The SwarmUI URL must be an HTTP(S) URL without credentials, query, or fragment.");
        }

        if (instance.SearchPaths is not { Count: > 0 } || instance.SearchPaths.Count > SwarmUIInstance.MaximumSearchPaths)
            throw new InvalidDataException($"Configure between 1 and {SwarmUIInstance.MaximumSearchPaths} search paths.");
        var paths = instance.SearchPaths.Select(SwarmUIPaths.NormalizeSearchPath).ToArray();
        if (paths.Select(path => path.Path).Distinct(StringComparer.Ordinal).Count() != paths.Length)
            throw new InvalidDataException("Each search path can only be configured once.");

        return instance with
        {
            Name = instance.Name.Trim(),
            BaseUrl = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/"),
            SearchPaths = paths,
            InstallationFolder = string.IsNullOrWhiteSpace(instance.InstallationFolder) ? null : instance.InstallationFolder.Trim(),
            StartWithApp = instance.StartWithApp && instance.IsManaged,
            ListenOnLocalNetwork = instance.ListenOnLocalNetwork && instance.IsManaged
        };
    }

    private static SwarmUIInstance ToInstance(StoredInstance item) => new(item.Id, item.Name, new Uri(item.BaseUrl),
        item.SearchPaths is null ? SwarmUIInstance.DefaultSearchPaths : item.SearchPaths.Select(path => new SwarmUISearchPath(path.Path, path.Depth)).ToArray(),
        item.InstallationFolder, item.Port, item.StartWithApp, item.ListenOnLocalNetwork);

    private async Task<List<StoredInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var stored = await JsonSerializer.DeserializeAsync<List<StoredInstance>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException(InvalidFileMessage);
            for (var index = 0; index < stored.Count; index++)
            {
                var item = stored[index];
                if (item is null || item.Name is null || item.BaseUrl is null || item.SearchPaths?.Any(path => path is null) == true ||
                    !Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out _) || item.Port is < 1 or > 65535)
                {
                    throw new InvalidDataException(InvalidFileMessage);
                }

                // Managed folder checks run on save and start so a moved installation does not hide every instance.
                var normalized = Normalize(ToInstance(item));
                stored[index] = item with { Name = normalized.Name, BaseUrl = normalized.BaseUrl.AbsoluteUri,
                    SearchPaths = normalized.SearchPaths.Select(path => new StoredSearchPath(path.Path, path.Depth)).ToList() };
            }

            if (stored.Select(item => item.Id).Distinct().Count() != stored.Count)
                throw new InvalidDataException(InvalidFileMessage);
            return stored;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or InvalidDataException)
        {
            throw new InvalidDataException(InvalidFileMessage);
        }
    }

    private async Task WriteAsync(List<StoredInstance> stored, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record StoredSearchPath(string Path, int Depth = SwarmUISearchPath.DefaultDepth);

    private sealed record StoredInstance(Guid Id, string Name, string BaseUrl, List<StoredSearchPath>? SearchPaths = null,
        string? InstallationFolder = null, int Port = SwarmUIInstance.DefaultPort, bool StartWithApp = false, bool ListenOnLocalNetwork = false);
}
