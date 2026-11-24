using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PersonalServerManager.LocalLibraries;

public sealed class CalibreCoverWriter(ManagedCalibreServer? managedCalibre = null)
{
    private const int MaximumImageBytes = 32 * 1024 * 1024;
    private const string ImageCheck = """
        import os, sys
        from qt.core import QImageReader
        QImageReader.setAllocationLimit(128)
        reader = QImageReader(os.environ['PSM_COVER_PATH'])
        kind = bytes(reader.format()).decode('ascii').lower()
        size = reader.size()
        if kind != os.environ['PSM_COVER_FORMAT'] or not size.isValid() or size.width() > 8192 or size.height() > 8192 or size.width() * size.height() > 40000000 or reader.read().isNull():
            sys.exit(1)
        """;
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "EPUB", "PDF", "AZW3", "MOBI", "AZW", "FB2", "MD", "MARKDOWN", "TXT", "RTF", "DOCX", "ODT", "DJVU",
        "CBZ", "CBR", "CB7", "CBT", "CBC"
    };
    private static readonly string[] CalibreProcesses = ["calibre", "calibre-server", "calibredb", "calibre-debug", "calibre-parallel", "ebook-viewer", "ebook-edit"];

    public async Task ApplyAsync(LocalLibraryInstance instance, string itemId, byte[] image, string contentType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsManaged || instance.ContentServerLibraryId is not null)
            throw new InvalidOperationException("Only managed local Calibre libraries can be modified.");
        ArgumentNullException.ThrowIfNull(itemId);
        var (folder, bookId) = ParseItemId(instance, itemId);
        ArgumentNullException.ThrowIfNull(image);
        var format = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpeg",
            "image/png" => "png",
            _ => throw new InvalidDataException("A JPEG or PNG cover is required.")
        };
        if (image.Length == 0 || image.Length > MaximumImageBytes)
            throw new InvalidDataException("The cover must be between 1 byte and 32 MiB.");

        var root = LocalLibraryPaths.Root(instance.RootPaths[folder]);
        var databasePath = LocalLibraryPaths.Resolve(root, "metadata.db");
        var server = managedCalibre?.GetWriterConnection(instance, folder);
        if (server is null && (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-journal")))
            throw new InvalidOperationException("The Calibre database has a pending journal. Close Calibre and resolve it before applying a cover.");
        await using (var guard = LocalLibraryPaths.OpenRead(root, databasePath, FileShare.ReadWrite))
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(databasePath).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT path FROM books WHERE id = $id";
            command.Parameters.AddWithValue("$id", bookId);
            var bookPath = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidDataException("The Calibre book was not found.");
            var directory = LocalLibraryPaths.Resolve(root, bookPath);
            if (!Directory.Exists(directory)) throw new InvalidDataException("The Calibre book folder is unavailable.");
            foreach (var name in new[] { "cover.jpg", "metadata.opf" })
                if (File.Exists(Path.Combine(directory, name))) LocalLibraryPaths.Resolve(root, bookPath, name);
        }

        var installation = LocalLibraryPaths.Root(instance.CalibreInstallationFolder ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2"));
        if (!File.Exists(Path.Combine(installation, "calibredb.exe")) || !File.Exists(Path.Combine(installation, "calibre-debug.exe")))
            throw new InvalidDataException("The Calibre installation must contain calibredb.exe and calibre-debug.exe.");
        var databaseTool = LocalLibraryPaths.Resolve(installation, "calibredb.exe");
        var debugTool = LocalLibraryPaths.Resolve(installation, "calibre-debug.exe");
        if (server is null) EnsureCalibreStopped();

        var temporaryDirectory = Directory.CreateTempSubdirectory("psm-cover-").FullName;
        try
        {
            var temporaryCover = Path.Combine(temporaryDirectory, "cover." + (format == "jpeg" ? "jpg" : "png"));
            await File.WriteAllBytesAsync(temporaryCover, image, cancellationToken);
            var check = CreateStartInfo(debugTool);
            check.ArgumentList.Add("-c");
            check.ArgumentList.Add(ImageCheck);
            check.Environment["PSM_COVER_PATH"] = temporaryCover;
            check.Environment["PSM_COVER_FORMAT"] = format;
            if (await RunAsync(check, cancellationToken) != 0)
                throw new InvalidDataException("The cover is not a valid JPEG or PNG of the declared type, or exceeds the image dimensions limit.");
            var start = CreateStartInfo(databaseTool);
            start.ArgumentList.Add("set_metadata");
            if (server is null)
            {
                EnsureCalibreStopped();
                start.ArgumentList.Add("--with-library");
                start.ArgumentList.Add(root);
            }
            else
            {
                var passwordFile = Path.Combine(temporaryDirectory, "password");
                await File.WriteAllTextAsync(passwordFile, server.Password, cancellationToken);
                foreach (var argument in new[] { "--with-library", server.LibraryUrl, "--username", server.Username, "--password", $"<f:{passwordFile}>" })
                    start.ArgumentList.Add(argument);
            }
            foreach (var argument in new[] { "--field", "cover:" + temporaryCover, bookId.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            if (await RunAsync(start, CancellationToken.None) != 0)
                throw new InvalidOperationException(server is null
                    ? "Calibre could not update the cover. Check library permissions, book availability, and that Calibre is stopped."
                    : "The managed Calibre server could not update the cover. Check the server status and book availability.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    private static (int Folder, long BookId) ParseItemId(LocalLibraryInstance instance, string itemId)
    {
        var folder = 0;
        var separator = itemId.IndexOf('~');
        if (separator >= 0)
        {
            if (!int.TryParse(itemId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out folder)
                || folder < 1 || folder >= instance.RootPaths.Count) throw new InvalidDataException("Invalid Calibre folder ID.");
            itemId = itemId[(separator + 1)..];
        }
        var parts = itemId.Split(':');
        if (parts.Length > 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var bookId)
            || bookId <= 0 || (parts.Length == 2 && !Formats.Contains(parts[1])))
            throw new InvalidDataException("Invalid Calibre book ID.");
        return (folder, bookId);
    }

    private static void EnsureCalibreStopped()
    {
        foreach (var name in CalibreProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length != 0)
                    throw new InvalidOperationException("Stop all Calibre applications and servers before applying a cover.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("CALIBRE_NO_SI_DANGER_DANGER");
        return start;
    }

    private static async Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Calibre could not be started."); }
        catch (Win32Exception exception) { throw new InvalidOperationException("Calibre could not be started. Check the installation and permissions.", exception); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
    }
}