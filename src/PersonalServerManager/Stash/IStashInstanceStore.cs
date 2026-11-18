namespace PersonalServerManager.Stash;

public interface IStashInstanceStore
{
    Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}