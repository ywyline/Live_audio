namespace TikTokAudio.Infrastructure.Tts.Cache;

internal static class CachePaths
{
    public static string Root(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("必须选择绝对目录。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Check(full);
        return full;
    }

    public static bool IsInside(string root, string path)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string OwnedSource(string staging, string path, bool mustExist)
    {
        var full = Root(path);
        if (!IsInside(staging, full))
            throw new InvalidDataException("音频必须位于已选择的生成暂存目录。");
        Check(staging);
        Check(full);
        if (Directory.Exists(full) || (mustExist && !File.Exists(full)))
            throw new InvalidDataException("生成音频文件无效。");
        return full;
    }

    // Recheck at each filesystem boundary; never follow junctions or symbolic links.
    public static void Check(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        if (full.AsSpan(root.Length).Contains(':'))
            throw new InvalidDataException("不支持备用数据流路径。");
        for (string? part = full; part is not null; part = Directory.GetParent(part)?.FullName)
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不支持重解析路径。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void DeleteSource(string staging, string path)
    {
        var owned = OwnedSource(staging, path, mustExist: false);
        File.Delete(owned);
    }

    public static void DeleteEntry(string root, string directory)
    {
        if (!IsInside(root, directory)) throw new InvalidDataException("缓存路径无效。");
        Check(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var name in new[] { "audio.wav", "metadata.json" })
        {
            var path = Path.Combine(directory, name);
            Check(path);
            File.Delete(path);
        }
        Directory.Delete(directory, recursive: false);
    }
}
