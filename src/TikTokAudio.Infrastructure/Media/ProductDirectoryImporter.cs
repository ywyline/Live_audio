using System.Globalization;
using System.Runtime.InteropServices;
using TikTokAudio.Application.Media;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;

namespace TikTokAudio.Infrastructure.Media;

public sealed class ProductDirectoryImporter
{
    private readonly ProductDirectoryImportOptions options;
    private readonly AudioOutputOptions audioOptions;

    public ProductDirectoryImporter(ProductDirectoryImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        audioOptions = new AudioOutputOptions
        {
            MaterialDirectory = options.RootDirectory,
            CacheDirectory = options.ValidationCacheDirectory,
            DeviceId = "import-validation-no-device",
            MaxInputBytes = options.MaxInputBytes,
            MaxDecodedBytes = options.MaxDecodedBytes,
            MaxPreparedSessions = 1
        };
        AudioFilePreparation.ValidateOptions(audioOptions);
        this.options = options with { RootDirectory = Path.GetFullPath(options.RootDirectory) };
    }

    public Task<ProductDirectoryImport> ImportAsync(
        IReadOnlyDictionary<int, string> productMappings,
        IReadOnlyDictionary<string, int>? clipProductOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productMappings);
        cancellationToken.ThrowIfCancellationRequested();
        if (productMappings.Count > options.MaxEntries || clipProductOverrides?.Count > options.MaxEntries)
            return Task.FromResult(new ProductDirectoryImport(options.RootDirectory,
                Array.AsReadOnly(Array.Empty<ImportedAudioProduct>()),
                Array.AsReadOnly(new[] { new MediaImportIssue(MediaImportIssueCode.ResourceLimit, ".", null,
                    "映射或片段元数据条目超过配置上限。") })));
        // Snapshot operator input before asynchronous file validation.
        var mappings = productMappings.ToDictionary(pair => pair.Key, pair => pair.Value);
        var overrides = clipProductOverrides?.ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<string, int>();
        return Task.Run(() => new ImportSession(options, audioOptions, mappings, overrides)
            .RunAsync(cancellationToken), cancellationToken);
    }

    private sealed class ImportSession(
        ProductDirectoryImportOptions options,
        AudioOutputOptions audioOptions,
        Dictionary<int, string> mappings,
        Dictionary<string, int> overrides)
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".mp3", ".aiff", ".aif", ".wma", ".m4a", ".aac" };
        private readonly List<MediaImportIssue> issues = [];
        private readonly HashSet<string> seenClips = new(StringComparer.Ordinal);
        private int entryCount;
        private bool limitReached;

        public async Task<ProductDirectoryImport> RunAsync(CancellationToken token)
        {
            var products = new List<ImportedAudioProduct>();
            if (!Directory.Exists(options.RootDirectory))
                Add(MediaImportIssueCode.InvalidRoot, options.RootDirectory, null, "指定素材根目录不存在或不可访问。");
            else
            {
                foreach (var product in NumberedDirectories(options.RootDirectory, null, token))
                {
                    token.ThrowIfCancellationRequested();
                    string? mapping = Mapping(product.Number, product.Path, product.Number);
                    var groups = new List<ImportedAudioGroup>();
                    foreach (var group in NumberedDirectories(product.Path, product.Number, token))
                    {
                        var clips = new List<ImportedAudioClip>();
                        foreach (string path in Entries(group.Path, product.Number, token))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!SafeEntry(path, product.Number, out bool isDirectory)) continue;
                            if (isDirectory)
                            {
                                Add(MediaImportIssueCode.UnexpectedEntry, path, product.Number, "子组中不能嵌套目录。");
                                continue;
                            }
                            string relative = Relative(path);
                            seenClips.Add(relative);
                            int? productOverride = null;
                            if (overrides.TryGetValue(relative, out int target))
                            {
                                productOverride = target;
                                if (target <= 0)
                                    Add(MediaImportIssueCode.InvalidMetadata, path, product.Number, "片段覆盖产品编号必须为正整数。");
                                else
                                    Mapping(target, path, product.Number);
                            }
                            if (!Extensions.Contains(Path.GetExtension(path)))
                            {
                                Add(MediaImportIssueCode.UnsupportedFile, path, product.Number, "文件格式不受支持。");
                                continue;
                            }
                            try
                            {
                                using var prepared = await AudioFilePreparation.PrepareAsync(audioOptions, path, token)
                                    .ConfigureAwait(false);
                                var asset = new LocalAudioAsset(path, Path.GetExtension(path)[1..].ToLowerInvariant(),
                                    "prerecorded", TimeSpan.FromSeconds((double)prepared.LengthSamples / prepared.SampleRate),
                                    prepared.SampleRate);
                                clips.Add(new ImportedAudioClip(relative, asset, productOverride));
                            }
                            catch (Exception error) when (IsFileFailure(error))
                            {
                                Add(MediaImportIssueCode.InvalidAudio, path, product.Number, "音频无法完整解码，或超出素材/缓存限制。");
                            }
                        }
                        if (clips.Count == 0)
                            Add(MediaImportIssueCode.EmptyDirectory, group.Path, product.Number, "子组没有可用音频。");
                        groups.Add(new ImportedAudioGroup(group.Number, Relative(group.Path), clips.AsReadOnly()));
                    }
                    if (groups.Count == 0)
                        Add(MediaImportIssueCode.EmptyDirectory, product.Path, product.Number, "产品没有可用子组。");
                    products.Add(new ImportedAudioProduct(product.Number, Relative(product.Path), mapping,
                        groups.AsReadOnly(), false));
                }
                if (products.Count == 0)
                    Add(MediaImportIssueCode.EmptyDirectory, options.RootDirectory, null, "根目录没有可用产品。");
            }
            foreach (string key in overrides.Keys)
            {
                token.ThrowIfCancellationRequested();
                if (!seenClips.Contains(key))
                    issues.Add(new MediaImportIssue(MediaImportIssueCode.InvalidMetadata, key, null,
                        "片段元数据未匹配已导入文件；须使用根目录相对路径和正斜杠。"));
            }
            token.ThrowIfCancellationRequested();
            bool globalProblem = issues.Any(issue => issue.ProductNumber is null);
            var blockedProducts = issues.Where(issue => issue.ProductNumber.HasValue)
                .Select(issue => issue.ProductNumber!.Value).ToHashSet();
            return new ProductDirectoryImport(options.RootDirectory,
                products.Select(product => product with
                {
                    CanStart = !globalProblem && !blockedProducts.Contains(product.Number)
                }).ToList().AsReadOnly(), issues.AsReadOnly());
        }

        private List<(int Number, string Path)> NumberedDirectories(string directory, int? product, CancellationToken token)
        {
            var result = new List<(int Number, string Path)>();
            foreach (string path in Entries(directory, product, token))
            {
                if (!SafeEntry(path, product, out bool isDirectory)) continue;
                if (!isDirectory)
                {
                    Add(MediaImportIssueCode.UnexpectedEntry, path, product, "产品和子组层级只允许编号目录。");
                    continue;
                }
                string name = Path.GetFileName(path);
                if (!name.All(character => character is >= '0' and <= '9') ||
                    !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
                {
                    Add(MediaImportIssueCode.InvalidNumber, path, product, "目录名必须为范围内的正整数编号。");
                    continue;
                }
                result.Add((number, path));
            }
            foreach (var duplicate in result.GroupBy(entry => entry.Number).Where(group => group.Count() > 1))
                foreach (var entry in duplicate)
                    Add(MediaImportIssueCode.DuplicateNumber, entry.Path, product ?? entry.Number, "目录编号重复（包括前导零别名）。");
            return result.OrderBy(entry => entry.Number).ThenBy(entry => entry.Path, StringComparer.Ordinal).ToList();
        }

        private List<string> Entries(string directory, int? product, CancellationToken token)
        {
            var result = new List<string>();
            if (limitReached) return result;
            try
            {
                AudioFilePreparation.RejectReparsePoints(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (entryCount >= options.MaxEntries)
                    {
                        limitReached = true;
                        Add(MediaImportIssueCode.ResourceLimit, options.RootDirectory, null, "目录项总数超过配置上限；导入结果不可启动。");
                        break;
                    }
                    entryCount++;
                    result.Add(path);
                }
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                Add(MediaImportIssueCode.IoFailure, directory, product, "目录无法安全读取。");
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private bool SafeEntry(string path, int? product, out bool isDirectory)
        {
            isDirectory = false;
            try
            {
                AudioFilePreparation.RejectReparsePoints(path);
                isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                return true;
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                Add(MediaImportIssueCode.UnsafePath, path, product, "目录项不可访问或含重解析链接。");
                return false;
            }
        }

        private string? Mapping(int number, string path, int? product)
        {
            if (mappings.TryGetValue(number, out string? id) && !string.IsNullOrWhiteSpace(id)) return id;
            Add(MediaImportIssueCode.MissingMapping, path, product, $"产品 {number} 缺少显式平台商品标识映射。");
            return null;
        }

        private string Relative(string path) => Path.GetRelativePath(options.RootDirectory, path).Replace('\\', '/');

        private void Add(MediaImportIssueCode code, string path, int? product, string detail) =>
            issues.Add(new MediaImportIssue(code, Relative(path), product, detail));

        private static bool IsFileFailure(Exception error) => error is
            IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            InvalidOperationException or COMException or NAudio.MmException;
    }
}
