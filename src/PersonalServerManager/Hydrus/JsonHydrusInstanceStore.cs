using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace PersonalServerManager.Hydrus;

public sealed class JsonHydrusInstanceStore(string filePath, IDataProtector protector) : IHydrusInstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            return stored.Select(item => new HydrusInstance(item.Id, item.Name, new Uri(item.ApiEndpoint),
                    item.ProtectedApiKey is null ? null : protector.Unprotect(item.ProtectedApiKey),
                    item.InstallationFolder, item.StartWithApp))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default)
    {
        HydrusClient.ValidateEndpoint(instance.ApiEndpoint);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await ReadAsync(cancellationToken);
            var existing = stored.FindIndex(item => item.Id == instance.Id);
            var previousApiKey = existing < 0 ? null : stored[existing].ProtectedApiKey;
            if (instance.IsManaged)
            {
                Management.ManagedBackendService.ValidateHydrusInstance(instance with
                {
                    ApiKey = string.IsNullOrWhiteSpace(instance.ApiKey) && previousApiKey is not null
                        ? protector.Unprotect(previousApiKey) : instance.ApiKey
                });
            }
            var apiKey = string.IsNullOrWhiteSpace(instance.ApiKey) ? previousApiKey : protector.Protect(instance.ApiKey);
            var replacement = new StoredInstance(instance.Id, instance.Name, instance.ApiEndpoint.AbsoluteUri, apiKey,
                instance.InstallationFolder, instance.StartWithApp);
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
            var stored = await JsonSerializer.DeserializeAsync<List<StoredInstance>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The Hydrus configuration file is empty.");
            foreach (var item in stored)
            {
                if (item is null || item.Name is null || !Uri.TryCreate(item.ApiEndpoint, UriKind.Absolute, out var endpoint))
                {
                    throw new InvalidDataException("The Hydrus configuration file is invalid. It was left untouched.");
                }

                HydrusClient.ValidateEndpoint(endpoint);
            }

            return stored;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("The Hydrus configuration file is invalid. It was left untouched.");
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

    private sealed record StoredInstance(Guid Id, string Name, string ApiEndpoint, string? ProtectedApiKey,
        string? InstallationFolder = null, bool StartWithApp = false);
}