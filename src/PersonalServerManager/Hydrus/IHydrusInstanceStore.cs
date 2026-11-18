namespace PersonalServerManager.Hydrus;

public interface IHydrusInstanceStore
{
    Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}