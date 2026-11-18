namespace PersonalServerManager.Security;

public interface IAdminCredentialStore
{
    Task<string?> GetPasswordHashAsync(CancellationToken cancellationToken = default);
    Task<bool> TryCreateAsync(string passwordHash, CancellationToken cancellationToken = default);
}