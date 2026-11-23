namespace PersonalServerManager.Stash;

public interface IStashInstanceStore
{
    Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default);
    Task UpdateAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Atomic updates are not supported by this store.");
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}