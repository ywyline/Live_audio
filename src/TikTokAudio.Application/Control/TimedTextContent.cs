using System.Text;

namespace TikTokAudio.Application.Control;

internal static class TimedTextContent
{
    private const int MaximumLength = 4096;

    public static string Normalize(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumLength)
            throw new ArgumentException("Message text must be at most 4096 UTF-16 characters.", nameof(text));
        foreach (var character in text)
        {
            if (char.IsControl(character) && !char.IsWhiteSpace(character))
                throw new ArgumentException("Message text cannot contain non-whitespace control characters.", nameof(text));
        }

        // Operator-authored text is not a speech template: prices and literal markers stay text.
        var normalized = text.Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length > MaximumLength)
            throw new ArgumentException("Normalized message text must be at most 4096 UTF-16 characters.", nameof(text));
        return normalized;
    }
}
