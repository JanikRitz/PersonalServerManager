namespace PersonalServerManager.LocalLibraries;

public enum LocalLibraryKind { Notes, CalibreText, CalibreComic, Calibre }

public sealed record LocalLibraryInstance(Guid Id, string Name, string RootPath, LocalLibraryKind Kind,
    Uri? ContentServerUrl = null, string? ContentServerLibraryId = null,
    int ManagedPort = 8080, bool StartWithApp = false, string? CalibreInstallationFolder = null,
    bool AutoGitPull = false)
{
    public bool IsCalibre => Kind is LocalLibraryKind.Calibre or LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic;
    public bool IsExternal => IsCalibre && string.IsNullOrWhiteSpace(RootPath) && ContentServerUrl is not null;
    public bool IsManaged => IsCalibre && !string.IsNullOrWhiteSpace(RootPath) && ContentServerUrl is null;

    public bool HasValidReaderConfiguration() =>
        ContentServerUrl is null && ContentServerLibraryId is null ||
        IsCalibre &&
        ContentServerUrl is { IsAbsoluteUri: true, Scheme: "http" or "https", UserInfo: "", Query: "", Fragment: "" } &&
        ContentServerLibraryId is { Length: > 0 and <= 200 } libraryId &&
        !libraryId.Any(char.IsControl) && !string.IsNullOrWhiteSpace(libraryId);

    public Uri? GetCalibreReaderUrl(string bookId, string format)
    {
        if (ContentServerUrl is null || !HasValidReaderConfiguration()) return null;
        var library = Uri.EscapeDataString(ContentServerLibraryId!);
        var book = Uri.EscapeDataString(bookId);
        var server = new Uri(ContentServerUrl.AbsoluteUri.TrimEnd('/') + "/");
        return format.Equals("PDF", StringComparison.OrdinalIgnoreCase)
            ? new Uri(server, $"get/pdf/{book}/{library}?content_disposition=inline")
            : new Uri(server, $"#mode=read_book&library_id={library}&book_id={book}&fmt={Uri.EscapeDataString(format.ToLowerInvariant())}");
    }
}

public interface ILocalLibraryStore
{
    Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}