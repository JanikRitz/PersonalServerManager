namespace PersonalServerManager.Hydrus;

public interface IHydrusInstanceStore
{
    Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
    Task UpdateAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Atomic updates are not supported by this store.");
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}