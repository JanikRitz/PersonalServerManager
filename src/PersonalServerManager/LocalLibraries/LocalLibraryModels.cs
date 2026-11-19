namespace PersonalServerManager.LocalLibraries;

public enum LocalLibraryKind { Notes, CalibreText, CalibreComic }

public sealed record LocalLibraryInstance(Guid Id, string Name, string RootPath, LocalLibraryKind Kind);

public interface ILocalLibraryStore
{
    Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}