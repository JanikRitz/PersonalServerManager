using System.Text.Json;

namespace PersonalServerManager.LocalLibraries;

public sealed class JsonLocalLibraryStore(string filePath) : ILocalLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(instance);
        LocalLibraryPaths.Root(instance.RootPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            var index = stored.FindIndex(item => item.Id == instance.Id);
            if (index < 0)
            {
                stored.Add(instance);
            }
            else
            {
                stored[index] = instance;
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

    private async Task<List<LocalLibraryInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var stored = await JsonSerializer.DeserializeAsync<List<LocalLibraryInstance>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The local library configuration is empty. It was left untouched.");
            foreach (var instance in stored)
            {
                Validate(instance);
            }

            if (stored.Select(instance => instance.Id).Distinct().Count() != stored.Count)
            {
                throw new InvalidDataException("The local library configuration contains duplicate IDs. It was left untouched.");
            }

            return stored;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local library configuration is invalid. It was left untouched.", exception);
        }
    }

    private static void Validate(LocalLibraryInstance? instance)
    {
        if (instance is null || instance.Id == Guid.Empty || string.IsNullOrWhiteSpace(instance.Name)
            || instance.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(instance.RootPath)
            || instance.RootPath.Any(char.IsControl) || !Enum.IsDefined(instance.Kind))
        {
            throw new InvalidDataException("Invalid local library configuration. It was left untouched.");
        }

        LocalLibraryPaths.NormalizeRoot(instance.RootPath);
    }

    private async Task WriteAsync(List<LocalLibraryInstance> stored, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}