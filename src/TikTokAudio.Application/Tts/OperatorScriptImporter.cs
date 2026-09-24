using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tts;

/// <summary>Imports trusted operator-authored UTF-8 scripts; never route live user content here.</summary>
public sealed class OperatorScriptImporter
{
    public const string Version = "operator-script-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TtsScriptImportLimits limits;

    /// <remarks>MaxSegmentLength counts UTF-16 code units, without splitting text elements.</remarks>
    public OperatorScriptImporter(TtsScriptImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxInputBytes <= 0 || limits.MaxSegments <= 0 || limits.MaxSegmentLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "导入限制必须全部为正整数。");
        this.limits = limits;
    }

    public TtsScriptImportResult Import(ReadOnlyMemory<byte> utf8, IReadOnlyDictionary<int, string> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        if (utf8.Length > limits.MaxInputBytes)
            return Failure("稿件超过允许的字节数。");

        string original;
        try
        {
            var bytes = utf8.Span;
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
            original = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Failure("稿件必须使用有效的 UTF-8 编码。");
        }

        if (original.Any(c => char.IsControl(c) && c is not '\t' and not '\r' and not '\n'))
            return Failure("稿件包含不支持的控制字符；请检查文本编码。");

        var segments = new List<TtsScriptSegment>();
        var warnings = new List<string>();
        var documentHash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(original)));
        ProductBoundary? pendingBoundary = null;
        var position = 0;
        while (position < original.Length)
        {
            var markerStart = original.IndexOf("@[", position, StringComparison.Ordinal);
            var textEnd = markerStart < 0 ? original.Length : markerStart;
            var error = AppendText(original[position..textEnd], segments, ref pendingBoundary);
            if (error is not null) return Failure(error);
            if (markerStart < 0) break;

            var markerEnd = original.IndexOf(']', markerStart + 2);
            if (markerEnd < 0) return Failure("商品标记缺少右方括号。");
            var numberText = original.AsSpan(markerStart + 2, markerEnd - markerStart - 2);
            if (numberText.IsEmpty || !IsAsciiDigits(numberText) ||
                !int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                return Failure("商品标记必须是 @[正整数]，且编号不能超出整数范围。");
            if (!products.TryGetValue(number, out var productId) || string.IsNullOrWhiteSpace(productId))
                return Failure("商品标记引用了未映射的产品编号。");

            // Position distinguishes repeated identical markers in the same document.
            var boundaryHash = SHA256.HashData(Encoding.UTF8.GetBytes(
                documentHash + ":" + markerStart.ToString(CultureInfo.InvariantCulture)));
            pendingBoundary = new ProductBoundary(productId, Convert.ToHexString(boundaryHash));
            position = markerEnd + 1;
        }

        if (pendingBoundary is not null)
            warnings.Add("稿件末尾的商品标记没有后续语音，不会触发商品边界。");
        if (segments.Count == 0) return Failure("稿件没有可合成的语音文字。", warnings);
        return new TtsScriptImportResult(OperationStatus.Succeeded,
            new TtsScriptDocument(original, Version, segments.AsReadOnly()), Array.Empty<string>(), warnings.AsReadOnly());
    }

    private string? AppendText(string text, List<TtsScriptSegment> segments, ref ProductBoundary? boundary)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        foreach (var sentence in SplitSentences(normalized))
        {
            var trimmed = TrimWhitespaceElements(sentence);
            if (trimmed.Length == 0) continue;
            var elements = StringInfo.ParseCombiningCharacters(trimmed);
            var elementIndex = 0;
            while (elementIndex < elements.Length)
            {
                var start = elements[elementIndex];
                var endIndex = elementIndex;
                var end = start;
                var lastWhitespace = -1;
                while (endIndex < elements.Length)
                {
                    var nextEnd = endIndex + 1 < elements.Length ? elements[endIndex + 1] : trimmed.Length;
                    if (nextEnd - start > limits.MaxSegmentLength) break;
                    if (IsWhitespaceElement(trimmed, elements[endIndex], nextEnd)) lastWhitespace = endIndex;
                    end = nextEnd;
                    endIndex++;
                }
                if (endIndex == elementIndex)
                    return "单个 Unicode 文本元素超过分段长度限制，无法安全切分。";

                // Prefer a word boundary when a length split is necessary.
                if (endIndex < elements.Length && lastWhitespace > elementIndex)
                {
                    endIndex = lastWhitespace + 1;
                    end = endIndex < elements.Length ? elements[endIndex] : trimmed.Length;
                }
                var value = TrimWhitespaceElements(trimmed[start..end]);
                if (value.Length > 0)
                {
                    if (segments.Count >= limits.MaxSegments) return "稿件超过允许的分段数量。";
                    segments.Add(new TtsScriptSegment(segments.Count, value, boundary));
                    boundary = null;
                }
                elementIndex = endIndex;
            }
        }
        return null;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var elements = StringInfo.ParseCombiningCharacters(text);
        var start = 0;
        for (var elementIndex = 0; elementIndex < elements.Length; elementIndex++)
        {
            var i = elements[elementIndex];
            var end = elementIndex + 1 < elements.Length ? elements[elementIndex + 1] : text.Length;
            if (text[i] is '\r' or '\n' or '\u2028' or '\u2029')
            {
                yield return text[start..i];
                start = end;
            }
            else if (IsSentenceEnd(text[i]) &&
                     !(text[i] == '.' && i > 0 && end < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[end])))
            {
                while (elementIndex + 1 < elements.Length &&
                       (IsSentenceEnd(text[elements[elementIndex + 1]]) || IsClosingQuote(text[elements[elementIndex + 1]])))
                    elementIndex++;
                end = elementIndex + 1 < elements.Length ? elements[elementIndex + 1] : text.Length;
                yield return text[start..end];
                start = end;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    private static string TrimWhitespaceElements(string text)
    {
        var elements = StringInfo.ParseCombiningCharacters(text);
        var first = 0;
        var last = elements.Length - 1;
        while (first <= last && IsWhitespaceElement(text, elements[first], first + 1 < elements.Length ? elements[first + 1] : text.Length)) first++;
        while (last >= first && IsWhitespaceElement(text, elements[last], last + 1 < elements.Length ? elements[last + 1] : text.Length)) last--;
        return first > last ? string.Empty : text[elements[first]..(last + 1 < elements.Length ? elements[last + 1] : text.Length)];
    }

    private static bool IsWhitespaceElement(string text, int start, int end)
    {
        for (var i = start; i < end; i++) if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
    private static bool IsSentenceEnd(char value) => value is '.' or '!' or '?' or '。' or '！' or '？';
    private static bool IsClosingQuote(char value) => value is '"' or '\'' or '”' or '’' or '»' or '」' or '』' or ')' or '）';
    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (c is < '0' or > '9') return false;
        return true;
    }

    private static TtsScriptImportResult Failure(string error, List<string>? warnings = null) =>
        new(OperationStatus.Failed, null, new[] { error }, warnings?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>());
}
