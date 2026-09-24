using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TikTokAudio.Application.Tts;

public static class TtsCacheKey
{
    public static string Create(string text, TtsGenerationSettings settings, string segmenterVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Identity);
        if (!double.IsFinite(settings.Rate) || settings.Rate <= 0 ||
            !double.IsFinite(settings.Pitch) || !double.IsFinite(settings.Volume) || settings.Volume < 0)
            throw new ArgumentException("合成参数必须是有效数值。", nameof(settings));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            Write(writer, "text", text, normalizeText: true);
            Write(writer, "engine", settings.Identity.EngineId);
            Write(writer, "engineVersion", settings.Identity.EngineVersion);
            Write(writer, "model", settings.Identity.ModelId);
            Write(writer, "modelVersion", settings.Identity.ModelVersion);
            Write(writer, "voice", settings.VoiceId);
            Write(writer, "language", settings.Language);
            writer.WriteNumber("rate", settings.Rate);
            writer.WriteNumber("pitch", settings.Pitch == 0 ? 0 : settings.Pitch);
            writer.WriteNumber("volume", settings.Volume == 0 ? 0 : settings.Volume);
            Write(writer, "segmenter", segmenterVersion);
            Write(writer, "dictionary", settings.PronunciationDictionaryVersion);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, string name, string value, bool normalizeText = false)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("缓存标识及文本不能为空。", name);
        var normalized = value.Normalize(NormalizationForm.FormC); // Also reject malformed UTF-16.
        writer.WriteString(name, normalizeText ? normalized : value);
    }
}
