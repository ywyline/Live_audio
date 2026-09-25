using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TikTokAudio.Infrastructure.Persistence;

public sealed class LocalDataPaths
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private LocalDataPaths(string rootDirectory)
    {
        RootDirectory = CanonicalAbsolutePath(rootDirectory);
        RejectReparsePoints(RootDirectory);
        DatabasePath = Path.Combine(RootDirectory, "state.sqlite3");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        LogDirectory = Path.Combine(RootDirectory, "logs");
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }

    public static LocalDataPaths ForDevelopment(string projectRoot) =>
        new(Path.Combine(CanonicalAbsolutePath(projectRoot), ".local-data"));

    public static LocalDataPaths ForUser(string localAppDataRoot, string productName)
    {
        ValidateSingleSegment(productName, nameof(productName));
        return new LocalDataPaths(Path.Combine(CanonicalAbsolutePath(localAppDataRoot), productName));
    }

    public static LocalDataPaths ForCurrentUser(string productName)
    {
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        return ForUser(localAppDataRoot, productName);
    }

    public void EnsureDirectories()
    {
        foreach (var directory in new[] { RootDirectory, CacheDirectory, LogDirectory })
        {
            ValidatePath(directory);
            Directory.CreateDirectory(directory);
            ValidatePath(directory);
        }
    }

    public string ValidatePath(string path)
    {
        var fullPath = CanonicalAbsolutePath(path);
        if (!string.Equals(fullPath, RootDirectory, PathComparison) &&
            !fullPath.StartsWith(RootDirectory + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("The path must remain inside the selected local data directory.", nameof(path));
        RejectReparsePoints(fullPath);
        return fullPath;
    }

    internal static void ValidateSingleSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." || value != value.Trim() || value.EndsWith('.') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\') ||
            value.Contains(':') || value.Any(char.IsControl))
            throw new ArgumentException("The name must be a single unambiguous path segment.", parameterName);
        var stem = value.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')
            throw new ArgumentException("Reserved device names are not valid local data names.", parameterName);
    }

    private static string CanonicalAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("A fully qualified local path is required.", nameof(path));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(path), canonical, PathComparison))
            throw new ArgumentException("The path must already be normalized without traversal segments.", nameof(path));
        var root = Path.GetPathRoot(canonical)!;
        foreach (var segment in canonical[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 0) ValidateSingleSegment(segment, nameof(path));
        }
        return canonical;
    }

    private static void RejectReparsePoints(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not allowed in local data paths.");
                if ((attributes & FileAttributes.Directory) == 0 && OperatingSystem.IsWindows())
                {
                    using var handle = File.OpenHandle(current, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (!GetFileInformationByHandle(handle, out var information))
                        throw new IOException("The local data file's link count could not be verified.");
                    if (information.NumberOfLinks > 1)
                        throw new IOException("Hard-linked files are not allowed in local data paths.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);
}
