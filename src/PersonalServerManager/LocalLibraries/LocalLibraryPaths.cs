using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PersonalServerManager.LocalLibraries;

internal static class LocalLibraryPaths
{
    internal static string Root(string path)
    {
        var root = NormalizeRoot(path);
        CheckAncestors(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The library directory is unavailable.");
        }

        return root;
    }

    internal static string NormalizeRoot(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] != '\\' || path[3..].Contains(':')
            || path[3..].Split('\\', '/').Any(part => part.Length > 0 && !IsSafePart(part)))
        {
            throw new InvalidDataException("A fully qualified Windows drive path is required.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string Resolve(string root, params string[] segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || Path.IsPathRooted(segment)
                || segment.Any(char.IsControl) || segment.Contains(':')
                || segment.Split('\\', '/').Any(part => !IsSafePart(part)))
            {
                throw new InvalidDataException("Invalid library-relative path.");
            }
        }

        var fullPath = Path.GetFullPath(Path.Combine([root, .. segments]));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The item is outside its library.");
        }

        CheckAncestors(fullPath);
        return fullPath;
    }

    internal static FileStream OpenRead(string root, string path, FileShare share = FileShare.Read)
    {
        Root(root);
        var safePath = Resolve(root, Path.GetRelativePath(root, path));
        using var rootHandle = CreateFile(root, 0x80, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (rootHandle.IsInvalid) throw new IOException("The library directory could not be opened safely.");
        var expectedPath = FinalPath(rootHandle).TrimEnd('\\') + "\\" + Path.GetRelativePath(root, safePath);
        Root(root);
        var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, share, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (!FinalPath(stream.SafeFileHandle).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The opened file is outside its expected library path.");
            }

            Resolve(root, Path.GetRelativePath(root, safePath));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new IOException("The library path could not be resolved safely.");
        return path.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    private static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Library paths cannot contain reparse points.");
            }
        }
    }

    private static bool IsSafePart(string part)
    {
        if (string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
            || part.Any(char.IsControl) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var stem = part.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'));
    }
}