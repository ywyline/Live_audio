using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Interactions;

public enum InteractionKind
{
    Welcome,
    Follow,
    Keyword
}

public enum InteractionChannel
{
    Voice,
    Text
}

public enum KeywordMatchMode
{
    Exact,
    Contains
}

public enum KeywordMatchRequirement
{
    Any,
    All
}

public enum InteractionAdmissionStatus
{
    Accepted,
    DuplicateUser,
    DuplicateEvent,
    NoMatch,
    Invalid,
    Ignored
}

public sealed record KeywordRule(
    string RuleId,
    IReadOnlyList<string> Keywords,
    KeywordMatchMode MatchMode = KeywordMatchMode.Contains,
    KeywordMatchRequirement Requirement = KeywordMatchRequirement.Any,
    int Priority = 0,
    string Template = InteractionTemplates.DefaultKeyword,
    TimeSpan? Cooldown = null,
    bool VoiceEnabled = true,
    bool TextEnabled = true)
{
    public bool Enabled { get; init; } = true;
    public int Order { get; init; }
    public TimeSpan TextCooldown { get; init; } = TimeSpan.Zero;
    public IReadOnlyList<string>? Templates { get; init; }
    public IReadOnlyList<string>? VoiceTemplates { get; init; }
    public IReadOnlyList<string>? TextTemplates { get; init; }

    public KeywordRule(string ruleId, IEnumerable<string> keywords, KeywordMatchMode matchMode = KeywordMatchMode.Contains,
        KeywordMatchRequirement requirement = KeywordMatchRequirement.Any, int priority = 0,
        string template = InteractionTemplates.DefaultKeyword, TimeSpan? cooldown = null,
        bool voiceEnabled = true, bool textEnabled = true)
        : this(ruleId, keywords?.ToArray() ?? throw new ArgumentNullException(nameof(keywords)), matchMode,
            requirement, priority, template, cooldown, voiceEnabled, textEnabled)
    {
        Validate();
    }

    // The coordinator also validates records built through the primary constructor or with expressions.
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RuleId);
        ArgumentNullException.ThrowIfNull(Keywords);
        if (Keywords.Count is < 1 or > 1000 || Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) ||
            keyword.Length > 4096 || InteractionTextSanitizer.MatchText(keyword).Length == 0))
            throw new ArgumentException("规则需要 1–1000 个有效关键词，每项最多 4096 字符。", nameof(Keywords));
        if (!Enum.IsDefined(MatchMode)) throw new ArgumentOutOfRangeException(nameof(MatchMode));
        if (!Enum.IsDefined(Requirement)) throw new ArgumentOutOfRangeException(nameof(Requirement));
        if (Cooldown is { } duration) InteractionPolicyOptions.ValidateCooldown(duration, nameof(Cooldown));
        InteractionPolicyOptions.ValidateCooldown(TextCooldown, nameof(TextCooldown));
        InteractionTemplates.Validate(Template);
        InteractionTemplates.ValidateCandidates(Templates);
        InteractionTemplates.ValidateCandidates(VoiceTemplates);
        InteractionTemplates.ValidateCandidates(TextTemplates);
    }

}

public sealed record InteractionPolicyOptions
{
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(3600);
    public static readonly TimeSpan DefaultWelcomeTtl = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultFollowTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultKeywordTtl = TimeSpan.FromSeconds(30);

    public TimeSpan WelcomeCooldown { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan FollowCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan KeywordCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TextCooldown { get; init; } = TimeSpan.Zero;
    public TimeSpan WelcomeTtl { get; init; } = DefaultWelcomeTtl;
    public TimeSpan FollowTtl { get; init; } = DefaultFollowTtl;
    public TimeSpan KeywordTtl { get; init; } = DefaultKeywordTtl;
    public int FollowCapacity { get; init; } = 20;
    public int MaxDisplayNameLength { get; init; } = 64;
    public int MaxCommentLength { get; init; } = 500;
    public bool VoiceEnabled { get; init; } = true;
    public bool TextEnabled { get; init; } = true;
    public string WelcomeTemplate { get; init; } = InteractionTemplates.DefaultWelcome;
    public string FollowTemplate { get; init; } = InteractionTemplates.DefaultFollow;
    public string KeywordTemplate { get; init; } = InteractionTemplates.DefaultKeyword;
    public IReadOnlyList<string>? WelcomeTemplates { get; init; }
    public IReadOnlyList<string>? FollowTemplates { get; init; }
    public IReadOnlyList<string>? KeywordTemplates { get; init; }

    public void Validate()
    {
        ValidateCooldown(WelcomeCooldown, nameof(WelcomeCooldown));
        ValidateCooldown(FollowCooldown, nameof(FollowCooldown));
        ValidateCooldown(KeywordCooldown, nameof(KeywordCooldown));
        ValidateCooldown(TextCooldown, nameof(TextCooldown));
        ValidateTtl(WelcomeTtl, nameof(WelcomeTtl));
        ValidateTtl(FollowTtl, nameof(FollowTtl));
        ValidateTtl(KeywordTtl, nameof(KeywordTtl));
        if (FollowCapacity is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(FollowCapacity));
        if (MaxDisplayNameLength is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(MaxDisplayNameLength));
        if (MaxCommentLength is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaxCommentLength));
        InteractionTemplates.Validate(WelcomeTemplate);
        InteractionTemplates.Validate(FollowTemplate);
        InteractionTemplates.Validate(KeywordTemplate);
        InteractionTemplates.ValidateCandidates(WelcomeTemplates);
        InteractionTemplates.ValidateCandidates(FollowTemplates);
        InteractionTemplates.ValidateCandidates(KeywordTemplates);
    }

    public static void ValidateCooldown(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero || value > MaxCooldown) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateTtl(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > MaxCooldown) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record InteractionCandidate(
    string CandidateId,
    InteractionKind Kind,
    string UserId,
    string DisplayName,
    string? Content,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? OccurredAt,
    string? RuleId,
    string RenderedText,
    bool VoiceEnabled,
    bool TextEnabled,
    TimeSpan TimeToLive,
    Guid? SessionId = null,
    string? RoomId = null,
    string? RenderedTextReply = null,
    string? SourceId = null,
    string? EventId = null,
    Guid? ReservationId = null,
    string? Fingerprint = null)
{
    public DateTimeOffset OrderingTime => (OccurredAt ?? ReceivedAt).ToUniversalTime();
}

public sealed record InteractionDiscard(InteractionCandidate Candidate, string Reason);

public sealed record InteractionDecision(
    InteractionCandidate Candidate,
    bool VoiceEnabled,
    bool TextEnabled,
    InteractionChannel? PreferredChannel = null)
{
    public InteractionKind Kind => Candidate.Kind;
    public string Text => PreferredChannel == InteractionChannel.Text ? Candidate.RenderedTextReply ?? Candidate.RenderedText : Candidate.RenderedText;
}

public sealed record InteractionAdmissionResult(
    InteractionAdmissionStatus Status,
    InteractionCandidate? Candidate = null,
    string? Detail = null)
{
    public bool Accepted => Status == InteractionAdmissionStatus.Accepted;
}

public sealed record InteractionQueueSnapshot(
    int WelcomeCount,
    int FollowCount,
    int KeywordCount,
    DateTimeOffset? WelcomeExpiresAt,
    DateTimeOffset? KeywordExpiresAt);

public static partial class InteractionTemplates
{
    public const string DefaultWelcome = "Chào mừng {userName} đến với buổi livestream!";
    public const string DefaultFollow = "Cảm ơn {userName} đã theo dõi!";
    public const string DefaultKeyword = "{userName} ơi, bạn hãy nhấn theo dõi nhé. Nếu có câu hỏi, bộ phận chăm sóc khách hàng sẽ hỗ trợ bạn.";

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static void Validate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (template.Length > 4096) throw new ArgumentOutOfRangeException(nameof(template));
        var remainder = Placeholder().Replace(template, match =>
        {
            if (match.Groups[1].Value is not ("userName" or "displayName" or "comment" or "content"))
                throw new ArgumentException("模板占位符无效或未知。", nameof(template));
            return string.Empty;
        });
        if (remainder.Contains('{') || remainder.Contains('}'))
            throw new ArgumentException("模板占位符无效或未知。", nameof(template));
    }

    public static void ValidateCandidates(IReadOnlyList<string>? templates)
    {
        if (templates is null) return;
        if (templates.Count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(templates));
        foreach (var template in templates) Validate(template);
    }

    public static string Render(string template, string displayName, string? content = null, int maxDisplayNameLength = 64, int maxContentLength = 500)
    {
        Validate(template);
        var safeName = InteractionTextSanitizer.Sanitize(displayName, maxDisplayNameLength);
        var safeContent = InteractionTextSanitizer.Sanitize(content ?? string.Empty, maxContentLength);
        // A single substitution pass keeps user-supplied placeholder text inert.
        var rendered = Placeholder().Replace(template, match => match.Groups[1].Value switch
        {
            "userName" or "displayName" => safeName,
            "comment" or "content" => safeContent,
            _ => throw new ArgumentException("模板占位符无效或未知。", nameof(template))
        });
        return InteractionTextSanitizer.Sanitize(rendered, int.MaxValue);
    }
}

public static partial class InteractionTextSanitizer
{
    [GeneratedRegex(@"@\s*\[\s*(?:\d\s*)+\]", RegexOptions.CultureInvariant)]
    private static partial Regex ProductMarker();

    [GeneratedRegex(@"<[^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    public static string MatchText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            // Replacement runes retain malformed UTF-16 as a boundary instead of joining words.
            // Matching preserves literal symbols and markers; only rendering sanitizes them.
            builder.Append(rune.ToString());
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeForMatch(string? value) => MatchText(value);

    public static string Sanitize(string? value, int maxLength)
    {
        if (maxLength <= 0 || string.IsNullOrEmpty(value)) return string.Empty;

        // Enumerating malformed UTF-16 yields replacement runes; filtering them before
        // normalization prevents Normalize from throwing on external event content.
        var filtered = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                filtered.Append(' ');
                continue;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (rune == Rune.ReplacementChar || category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                continue;
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.CurrencySymbol ||
                category == UnicodeCategory.MathSymbol && rune.Value is not ('<' or '>'))
                continue;
            filtered.Append(rune.ToString());
        }

        // Remove remote markers, including whitespace-obfuscated ones, before truncation.
        var clean = HtmlTag().Replace(filtered.ToString(), string.Empty);
        while (ProductMarker().IsMatch(clean)) clean = ProductMarker().Replace(clean, string.Empty);
        clean = clean.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(Math.Min(clean.Length, maxLength));
        var count = 0;
        var pendingSpace = false;
        foreach (var rune in clean.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                if (count + 1 >= maxLength) break;
                builder.Append(' ');
                count++;
                pendingSpace = false;
            }
            if (count >= maxLength) break;
            builder.Append(rune.ToString());
            count++;
        }
        return builder.ToString();
    }
}
