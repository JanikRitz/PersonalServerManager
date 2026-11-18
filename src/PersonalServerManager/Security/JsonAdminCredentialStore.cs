using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PersonalServerManager.Security;

public sealed class JsonAdminCredentialStore(string filePath, IDataProtector protector) : IAdminCredentialStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetPasswordHashAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var protectedHash = await File.ReadAllTextAsync(filePath, cancellationToken);
            try
            {
                return protector.Unprotect(protectedHash);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("The local administrator credential could not be decrypted.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryCreateAsync(string passwordHash, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(filePath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, protector.Protect(passwordHash), cancellationToken);
                try
                {
                    File.Move(temporaryPath, filePath);
                    return true;
                }
                catch (IOException) when (File.Exists(filePath))
                {
                    return false;
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}