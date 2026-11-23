using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

[CollectionDefinition("Calibre cover integration", DisableParallelization = true)]
public sealed class CalibreCoverIntegrationCollection;

[Collection("Calibre cover integration")]
public sealed class CalibreCoverWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c-").FullName;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task RejectsRemoteAndNonCalibreLibraries()
    {
        var writer = new CalibreCoverWriter();
        var remote = new LocalLibraryInstance(Guid.NewGuid(), "remote", "", LocalLibraryKind.Calibre,
            new Uri("http://localhost:8080/"), "library");
        var notes = remote with { Kind = LocalLibraryKind.Notes, ContentServerUrl = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyAsync(remote, "1", [1], "image/png"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyAsync(notes, "1", [1], "image/png"));
    }

    [Theory]
    [InlineData("../1")]
    [InlineData("1~../2")]
    [InlineData("0~1")]
    [InlineData("1;echo")]
    [InlineData("1:exe")]
    public async Task RejectsInvalidItemIds(string itemId)
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "local", Path.GetTempPath(), LocalLibraryKind.Calibre);
        await Assert.ThrowsAsync<InvalidDataException>(() => new CalibreCoverWriter().ApplyAsync(instance, itemId, [1], "image/png"));
    }

    [Fact]
    public async Task RejectsUnsupportedOrOversizedImagesBeforeOpeningLibrary()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "local", _root, LocalLibraryKind.Calibre);
        var writer = new CalibreCoverWriter();
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1", Png, "image/gif"));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1", [], "image/png"));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1", new byte[32 * 1024 * 1024 + 1], "image/png"));
    }

    [Fact]
    public async Task RejectsTraversalInBookDatabaseAndMissingBook()
    {
        var instance = await FixtureAsync("../outside");
        var writer = new CalibreCoverWriter();
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1", Png, "image/png"));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "2", Png, "image/png"));
    }

    [Fact]
    public async Task HonorsCancellationBeforeDoingWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "local", _root, LocalLibraryKind.Calibre);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CalibreCoverWriter().ApplyAsync(instance, "1", Png, "image/png", cancellation.Token));
    }

    [Fact]
    public async Task RejectsPendingJournalAndMissingCalibreTools()
    {
        var instance = await FixtureAsync("book");
        Directory.CreateDirectory(Path.Combine(_root, "book"));
        var journal = Path.Combine(_root, "metadata.db-wal");
        await File.WriteAllTextAsync(journal, "pending");
        var writer = new CalibreCoverWriter();
        var pending = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyAsync(instance, "1", Png, "image/png"));
        Assert.Contains("journal", pending.Message);
        File.Delete(journal);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.ApplyAsync(instance with { CalibreInstallationFolder = _root }, "1", Png, "image/png"));
        Assert.Contains("calibredb.exe", missing.Message);
    }

    [Fact]
    public async Task UpdatesOnlySelectedTemporaryLibraryAndRejectsBadImages()
    {
        var installation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2");
        var executable = Path.Combine(installation, "calibredb.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(executable) || !File.Exists(Path.Combine(installation, "calibre-debug.exe"))) return;
        var first = Directory.CreateDirectory(Path.Combine(_root, "first lib")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "second lib")).FullName;
        await RunCalibreAsync(executable, "add", "--with-library", first, "--empty", "--title", "First");
        await RunCalibreAsync(executable, "add", "--with-library", second, "--empty", "--title", "Second");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "local", first, LocalLibraryKind.Calibre,
            CalibreInstallationFolder: installation, AdditionalRootPaths: [second]);
        var writer = new CalibreCoverWriter();
        var firstBefore = await ReadCoverAsync(first);
        var secondBefore = await ReadCoverAsync(second);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1~1:EPUB", Png, "image/jpeg"));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyAsync(instance, "1~1", [137, 80, 78, 71], "image/png"));
        Assert.Equal(secondBefore, await ReadCoverAsync(second));
        await writer.ApplyAsync(instance, "1~1:EPUB", Png, "image/png");
        Assert.Equal(firstBefore, await ReadCoverAsync(first));
        Assert.NotEqual(secondBefore, await ReadCoverAsync(second));
        Assert.True(await HasCoverAsync(second));
        var jpeg = (await ReadCoverAsync(second))!;
        await writer.ApplyAsync(instance, "1", jpeg, "image/jpeg");
        Assert.True(await HasCoverAsync(first));
        Assert.Equal(jpeg, await ReadCoverAsync(second));
    }

    private async Task<LocalLibraryInstance> FixtureAsync(string bookPath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "metadata.db"), Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE books (id INTEGER PRIMARY KEY, path TEXT); INSERT INTO books VALUES (1, $path)";
        command.Parameters.AddWithValue("$path", bookPath);
        await command.ExecuteNonQueryAsync();
        return new LocalLibraryInstance(Guid.NewGuid(), "local", _root, LocalLibraryKind.Calibre);
    }

    private static async Task RunCalibreAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }

    private static async Task<string> BookPathAsync(string root)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "metadata.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM books WHERE id = 1";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]?> ReadCoverAsync(string root)
    {
        var path = Path.Combine(root, await BookPathAsync(root), "cover.jpg");
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    private static async Task<bool> HasCoverAsync(string root)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "metadata.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT has_cover FROM books WHERE id = 1";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    public void Dispose() => Directory.Delete(_root, true);
}