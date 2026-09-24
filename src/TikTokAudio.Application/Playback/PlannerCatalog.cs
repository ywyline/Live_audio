using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TikTokAudio.Application.Media;

namespace TikTokAudio.Application.Playback;

internal sealed record PlannerGroup(
    int ProductNumber, int Number, string GroupId, ImportedAudioClip[] Clips, string PlatformProductId);

internal sealed class PlannerCatalog
{
    public PlannerGroup[] Groups { get; }
    public IReadOnlyDictionary<int, string> Mappings { get; }
    public string Fingerprint { get; }

    public PlannerCatalog(ProductDirectoryImport catalog, IReadOnlyDictionary<int, string>? productMappings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsValid || catalog.Products.Any(product => !product.CanStart))
            throw new ArgumentException("计划只接受无预检查问题且可启动的明确素材集合。", nameof(catalog));
        var mappings = productMappings?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
        if (mappings.Any(pair => pair.Key <= 0 || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("商品映射无效。", nameof(productMappings));
        var groups = new List<PlannerGroup>();
        var products = new HashSet<int>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in catalog.Products.OrderBy(product => product.Number))
        {
            if (product.Number <= 0 || !products.Add(product.Number) ||
                string.IsNullOrWhiteSpace(product.PlatformProductId) || product.Groups.Count == 0)
                throw new ArgumentException("产品编号、映射或子组无效。", nameof(catalog));
            if (!product.Groups.Any(group => group.Number == 1))
                throw new ArgumentException("产品缺少用于商品进入的子组 1。", nameof(catalog));
            if (mappings.TryGetValue(product.Number, out string? mapped) && mapped != product.PlatformProductId)
                throw new ArgumentException("产品映射与预检查结果不一致。", nameof(productMappings));
            mappings[product.Number] = product.PlatformProductId;
            var groupNumbers = new HashSet<int>();
            foreach (var group in product.Groups.OrderBy(group => group.Number))
            {
                if (group.Number <= 0 || !groupNumbers.Add(group.Number) ||
                    string.IsNullOrWhiteSpace(group.RelativePath) || !groupIds.Add(group.RelativePath) || group.Clips.Count == 0)
                    throw new ArgumentException("子组编号、标识或素材无效。", nameof(catalog));
                var clips = group.Clips.OrderBy(clip => clip.ClipId, StringComparer.Ordinal).ToArray();
                foreach (var clip in clips)
                {
                    if (string.IsNullOrWhiteSpace(clip.ClipId) || !clipIds.Add(clip.ClipId) || clip.Asset is null ||
                        string.IsNullOrWhiteSpace(clip.Asset.Path) || clip.Asset.SampleRate is null or <= 0 ||
                        clip.Asset.Duration is null || clip.Asset.Duration <= TimeSpan.Zero || clip.ProductOverrideNumber <= 0)
                        throw new ArgumentException("片段标识、音频信息或覆盖编号无效。", nameof(catalog));
                }
                groups.Add(new PlannerGroup(product.Number, group.Number, group.RelativePath, clips, product.PlatformProductId));
            }
        }
        if (groups.SelectMany(group => group.Clips).Any(clip =>
            clip.ProductOverrideNumber is int target && !mappings.ContainsKey(target)))
            throw new ArgumentException("片段覆盖目标缺少显式商品映射。", nameof(productMappings));
        Groups = groups.ToArray();
        Mappings = mappings;
        Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Groups,
            Mappings = mappings.OrderBy(pair => pair.Key).ToArray()
        })));
    }

    public static string ProductId(PlannerGroup group) => group.ProductNumber.ToString(CultureInfo.InvariantCulture);
}
