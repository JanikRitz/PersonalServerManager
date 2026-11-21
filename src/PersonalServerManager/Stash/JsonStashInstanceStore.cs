using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace PersonalServerManager.Stash;

public sealed class JsonStashInstanceStore(string filePath, IDataProtector protector) : IStashInstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            return stored
                .Select(item => new StashInstance(
                    item.Id,
                    item.Name,
                    new Uri(item.GraphQlEndpoint),
                    new Uri(item.WebUiUrl),
                    item.ProtectedApiKey is null ? null : protector.Unprotect(item.ProtectedApiKey),
                    item.InstallationFolder, item.Port, item.StartWithApp))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default)
    {
        Management.ManagedBackendService.ValidateStashInstance(instance);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            var existing = stored.FindIndex(item => item.Id == instance.Id);
            var previousApiKey = existing < 0 ? null : stored[existing].ProtectedApiKey;
            var apiKey = string.IsNullOrWhiteSpace(instance.ApiKey)
                ? previousApiKey
                : protector.Protect(instance.ApiKey);
            var replacement = new StoredInstance(
                instance.Id,
                instance.Name,
                instance.GraphQlEndpoint.AbsoluteUri,
                instance.WebUiUrl.AbsoluteUri,
                apiKey, instance.InstallationFolder, instance.Port, instance.StartWithApp);

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

    private async Task<List<StoredInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<List<StoredInstance>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The Stash configuration file is empty.");
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException or FormatException)
        {
            throw new InvalidDataException("The Stash configuration file is invalid. It was left untouched.", exception);
        }
    }

    private async Task WriteAsync(List<StoredInstance> stored, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record StoredInstance(
        Guid Id,
        string Name,
        string GraphQlEndpoint,
        string WebUiUrl,
        string? ProtectedApiKey,
        string? InstallationFolder = null,
        int Port = 9999,
        bool StartWithApp = false);
}