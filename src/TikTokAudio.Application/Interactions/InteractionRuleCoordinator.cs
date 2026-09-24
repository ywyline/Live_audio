using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Interactions;

/// <summary>
/// Owns bounded interaction decisions for one explicitly selected room/session. Inputs must
/// already have passed T060 event deduplication and user reservation. No external work runs here.
/// </summary>
public sealed class InteractionRuleCoordinator
{
    public const int DiscardCapacity = 256;
    private readonly IClock clock;
    private readonly IRandomSource? random;
    private readonly InteractionPolicyOptions options;
    private readonly KeywordRule[] rules;
    private readonly object gate = new();
    private readonly List<Pending> pending = [];
    private readonly Queue<InteractionDiscard> discards = new();
    private readonly Dictionary<InteractionKind, long> voiceCompletedAt = [];
    private readonly Dictionary<string, long> ruleVoiceCompletedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> ruleTextCompletedAt = new(StringComparer.Ordinal);
    private long? textCompletedAt;
    private Pending? voiceLease;
    private Pending? textLease;
    private Guid? sessionId;
    private string? roomId;
    private long sequence;
    private bool stopped;

    public InteractionRuleCoordinator(IClock clock, IEnumerable<KeywordRule>? keywordRules = null,
        InteractionPolicyOptions? options = null, IRandomSource? random = null)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.random = random;
        var supplied = options ?? new InteractionPolicyOptions();
        supplied.Validate();
        this.options = supplied with
        {
            WelcomeTemplates = CopyTemplates(supplied.WelcomeTemplates),
            FollowTemplates = CopyTemplates(supplied.FollowTemplates),
            KeywordTemplates = CopyTemplates(supplied.KeywordTemplates)
        };
        rules = (keywordRules ?? []).Take(1001).Select(rule => { ArgumentNullException.ThrowIfNull(rule); rule.Validate(); return rule with
        {
            Keywords = Array.AsReadOnly(rule.Keywords.ToArray()),
            Templates = CopyTemplates(rule.Templates),
            VoiceTemplates = CopyTemplates(rule.VoiceTemplates),
            TextTemplates = CopyTemplates(rule.TextTemplates)
        }; }).ToArray();
        if (rules.Length > 1000) throw new ArgumentException("最多允许 1000 条关键词规则。", nameof(keywordRules));
        if (rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != rules.Length)
            throw new ArgumentException("规则 ID 不能重复。", nameof(keywordRules));
        foreach (var rule in rules)
        {
            ValidateTemplates(rule.Templates);
            ValidateTemplates(rule.VoiceTemplates);
            ValidateTemplates(rule.TextTemplates);
        }
        ValidateTemplates(this.options.WelcomeTemplates);
        ValidateTemplates(this.options.FollowTemplates);
        ValidateTemplates(this.options.KeywordTemplates);
    }

    public InteractionPolicyOptions Options => options;

    /// <summary>A real context change invalidates every lease and waiting decision. Same-context reconnect preserves them.</summary>
    public void SetContext(Guid nextSessionId, string nextRoomId)
    {
        if (nextSessionId == Guid.Empty) throw new ArgumentException("场次不能为空。", nameof(nextSessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(nextRoomId);
        lock (gate)
        {
            if (!stopped && sessionId == nextSessionId && roomId == nextRoomId) return;
            foreach (var item in pending.ToArray()) DiscardUnsafe(item, "ContextChanged");
            voiceLease = null;
            textLease = null;
            voiceCompletedAt.Clear();
            ruleVoiceCompletedAt.Clear();
            ruleTextCompletedAt.Clear();
            textCompletedAt = null;
            sessionId = nextSessionId;
            roomId = nextRoomId;
            stopped = false;
        }
    }

    /// <summary>Stops this context immediately; only an explicit SetContext enables admission again.</summary>
    public void Stop()
    {
        lock (gate)
        {
            foreach (var item in pending.ToArray()) DiscardUnsafe(item, "Released");
            voiceLease = null;
            textLease = null;
            voiceCompletedAt.Clear();
            ruleVoiceCompletedAt.Clear();
            ruleTextCompletedAt.Clear();
            textCompletedAt = null;
            stopped = true;
        }
    }
    public InteractionQueueSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                RemoveExpiredUnsafe();
                var waiting = pending.Where(item => item.VoicePending || (ReferenceEquals(voiceLease, item) && !item.VoiceStarted)).Select(item => item.Candidate).ToArray();
                var welcome = waiting.FirstOrDefault(candidate => candidate.Kind == InteractionKind.Welcome);
                var keyword = waiting.FirstOrDefault(candidate => candidate.Kind == InteractionKind.Keyword);
                return new(waiting.Count(candidate => candidate.Kind == InteractionKind.Welcome),
                    waiting.Count(candidate => candidate.Kind == InteractionKind.Follow),
                    waiting.Count(candidate => candidate.Kind == InteractionKind.Keyword),
                    welcome is null ? null : FindUnsafe(welcome)!.ExpiresAt,
                    keyword is null ? null : FindUnsafe(keyword)!.ExpiresAt);
            }
        }
    }

    /// <summary>Accepts an already deduplicated and reserved event; permanent business deduplication remains owned by T060.</summary>
    public InteractionAdmissionResult Enqueue(LiveEvent liveEvent, EventProcessResult? admission = null)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        lock (gate)
        {
            if (admission is not { ShouldProcess: true }) return Invalid("事件没有通过上游去重和占位检查。");
            if (stopped) return Invalid("互动规则已经停止；需要显式重新选择场次。");
            if (liveEvent.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(liveEvent.RoomId))
                return Invalid("事件场次或房间无效。");
            if (sessionId is null) SetContext(liveEvent.SessionId, liveEvent.RoomId);
            if (sessionId != liveEvent.SessionId || roomId != liveEvent.RoomId)
                return Invalid("事件不属于当前场次和房间。");
            RemoveExpiredUnsafe();
            if (liveEvent.EventType is not (LiveEventType.Enter or LiveEventType.Follow or LiveEventType.Comment))
                return new(InteractionAdmissionStatus.Ignored);
            if (liveEvent.EventType is LiveEventType.Enter or LiveEventType.Follow)
            {
                if (string.IsNullOrWhiteSpace(liveEvent.UserId)) return Invalid("互动缺少稳定用户 ID。");
                if (admission.Status != EventProcessStatus.Reserved || admission.ReservationId is null || admission.ReservationId == Guid.Empty)
                    return Invalid("欢迎或关注缺少有效的上游预留。");
            }
            if (pending.Count + discards.Count >= DiscardCapacity)
                return Invalid("丢弃通知等待消费，暂不接收新的互动。");
            var kind = liveEvent.EventType switch
            {
                LiveEventType.Enter => InteractionKind.Welcome,
                LiveEventType.Follow => InteractionKind.Follow,
                _ => InteractionKind.Keyword
            };
            if (kind != InteractionKind.Keyword && pending.Any(item => item.Candidate.Kind == kind && item.Candidate.UserId == liveEvent.UserId))
                return new(InteractionAdmissionStatus.DuplicateUser, Detail: "同类用户候选正在等待或执行。");
            var content = InteractionTextSanitizer.Sanitize(liveEvent.Content, options.MaxCommentLength);
            KeywordRule? rule = null;
            if (kind == InteractionKind.Keyword)
            {
                // Match the entire normalized input, never its truncated rendering: exact matching must not gain a false hit.
                var matchContent = InteractionTextSanitizer.MatchText(liveEvent.Content ?? string.Empty);
                rule = rules.Where(item => item.Enabled && ((options.VoiceEnabled && item.VoiceEnabled) || (options.TextEnabled && item.TextEnabled)) && Matches(item, matchContent))
                    .OrderByDescending(item => item.Priority).ThenBy(item => item.Order)
                    .ThenBy(item => item.RuleId, StringComparer.Ordinal).FirstOrDefault();
                if (rule is null) return new(InteractionAdmissionStatus.NoMatch);
            }
            var voice = options.VoiceEnabled && (rule?.VoiceEnabled ?? true);
            var text = kind == InteractionKind.Keyword && options.TextEnabled && (rule?.TextEnabled ?? false);
            if (!voice && !text) return new(InteractionAdmissionStatus.Ignored);
            var template = voice ? ChooseTemplate(kind, rule, InteractionChannel.Voice) : null;
            var textTemplate = text ? ChooseTemplate(kind, rule, InteractionChannel.Text) : null;
            var displayName = InteractionTextSanitizer.Sanitize(liveEvent.DisplayName, options.MaxDisplayNameLength);
            var candidate = new InteractionCandidate($"interaction:{++sequence}", kind, liveEvent.UserId ?? string.Empty, displayName,
                kind == InteractionKind.Keyword ? content : null, liveEvent.ReceivedAt.ToUniversalTime(), liveEvent.OccurredAt?.ToUniversalTime(),
                rule?.RuleId, template is null ? string.Empty : InteractionTemplates.Render(template, displayName, content, options.MaxDisplayNameLength, options.MaxCommentLength),
                voice, text, TimeToLive(kind), sessionId, roomId,
                textTemplate is null ? null : InteractionTemplates.Render(textTemplate, displayName, content, options.MaxDisplayNameLength, options.MaxCommentLength),
                liveEvent.SourceId, liveEvent.EventId, admission.ReservationId, admission.Fingerprint);
            var admittedUtc = clock.UtcNow;
            var age = admittedUtc - candidate.ReceivedAt;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var item = new Pending(candidate, clock.Now.Ticks, candidate.TimeToLive - age, admittedUtc);
            if (!FreshUnsafe(item))
            {
                discards.Enqueue(new(candidate, "Expired"));
                return new(InteractionAdmissionStatus.Accepted, candidate);
            }
            var waitingSameKind = pending.Where(entry => entry.Candidate.Kind == kind && !entry.VoiceStarted).ToArray();
            if (kind == InteractionKind.Follow)
            {
                if (waitingSameKind.Length >= options.FollowCapacity)
                    DiscardUnsafe(waitingSameKind[0], "Full");
            }
            else
            {
                var existing = waitingSameKind.LastOrDefault();
                var older = existing is not null && (kind == InteractionKind.Welcome
                    ? candidate.OrderingTime < existing.Candidate.OrderingTime
                    : candidate.ReceivedAt < existing.Candidate.ReceivedAt);
                if (older)
                {
                    discards.Enqueue(new(candidate, "Replaced"));
                    return new(InteractionAdmissionStatus.Accepted, candidate);
                }
                foreach (var previous in waitingSameKind) DiscardUnsafe(previous, "Replaced");
            }
            pending.Add(item);
            return new(InteractionAdmissionStatus.Accepted, candidate);
        }
    }

    public InteractionDecision? TryPrepareNext() => TrySelectNext();

    /// <summary>Creates one voice preparation lease; the host must BeginPlayback and then complete or release it.</summary>
    public InteractionDecision? TrySelectNext()
    {
        lock (gate)
        {
            RemoveExpiredUnsafe();
            if (voiceLease is not null) return null;
            var item = OrderedUnsafe().FirstOrDefault(item => item.VoicePending && EligibleUnsafe(item.Candidate, InteractionChannel.Voice));
            if (item is null) return null;
            item.VoicePending = false;
            voiceLease = item;
            return new(item.Candidate, true, false, InteractionChannel.Voice);
        }
    }

    public InteractionDecision? TrySelectTextNext()
    {
        lock (gate)
        {
            RemoveExpiredUnsafe();
            if (textLease is not null) return null;
            var item = OrderedUnsafe().FirstOrDefault(item => item.TextPending && EligibleUnsafe(item.Candidate, InteractionChannel.Text));
            if (item is null) return null;
            item.TextPending = false;
            textLease = item;
            return new(item.Candidate, false, true, InteractionChannel.Text);
        }
    }

    public bool IsFresh(InteractionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate) return CurrentUnsafe(candidate) && FreshUnsafe(candidate);
    }

    public bool CanPlay(InteractionCandidate candidate, InteractionChannel channel)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate)
        {
            RemoveExpiredUnsafe();
            var item = FindUnsafe(candidate);
            if (item is null || !EligibleUnsafe(candidate, channel)) return false;
            return channel == InteractionChannel.Voice
                ? ReferenceEquals(voiceLease, item) && !item.VoiceStarted
                : ReferenceEquals(textLease, item);
        }
    }

    public bool BeginPlayback(InteractionCandidate candidate)
    {
        lock (gate)
        {
            if (!CanPlay(candidate, InteractionChannel.Voice)) return false;
            voiceLease!.VoiceStarted = true;
            return true;
        }
    }

    /// <summary>Only the current started lease may complete; duplicate and late callbacks have no effect.</summary>
    public void MarkVoiceCompleted(InteractionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate)
        {
            var item = voiceLease;
            if (item is null || !ReferenceEquals(item.Candidate, candidate) || !item.VoiceStarted || !CurrentUnsafe(candidate)) return;
            voiceCompletedAt[candidate.Kind] = clock.Now.Ticks;
            if (candidate.RuleId is not null) ruleVoiceCompletedAt[candidate.RuleId] = clock.Now.Ticks;
            voiceLease = null;
            item.VoiceStarted = false;
            RemoveFinishedUnsafe(item);
        }
    }

    public void MarkTextCompleted(InteractionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate)
        {
            var item = textLease;
            if (item is null || !ReferenceEquals(item.Candidate, candidate) || !CurrentUnsafe(candidate)) return;
            textCompletedAt = clock.Now.Ticks;
            if (candidate.RuleId is not null) ruleTextCompletedAt[candidate.RuleId] = clock.Now.Ticks;
            textLease = null;
            RemoveFinishedUnsafe(item);
        }
    }

    public void MarkPlaybackCompleted(InteractionCandidate candidate, InteractionChannel channel)
    {
        if (channel == InteractionChannel.Voice) MarkVoiceCompleted(candidate);
        else if (channel == InteractionChannel.Text) MarkTextCompleted(candidate);
    }

    public void Release(InteractionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate)
        {
            var item = FindUnsafe(candidate);
            if (item is not null) DiscardUnsafe(item, "Released");
        }
    }

    /// <summary>Ends only the failed channel; the independent channel can still finish.</summary>
    public void Release(InteractionCandidate candidate, InteractionChannel channel)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (gate)
        {
            var item = FindUnsafe(candidate);
            if (item is null) return;
            if (channel == InteractionChannel.Voice)
            {
                item.VoicePending = false;
                item.VoiceStarted = false;
                if (ReferenceEquals(voiceLease, item)) voiceLease = null;
            }
            else
            {
                item.TextPending = false;
                if (ReferenceEquals(textLease, item)) textLease = null;
            }
            if (!item.VoicePending && !item.TextPending && !ReferenceEquals(voiceLease, item) && !ReferenceEquals(textLease, item))
                DiscardUnsafe(item, "Released");
        }
    }
    public IReadOnlyList<InteractionDiscard> DrainDiscards()
    {
        lock (gate)
        {
            RemoveExpiredUnsafe();
            var result = discards.ToArray();
            discards.Clear();
            return Array.AsReadOnly(result);
        }
    }

    private IEnumerable<Pending> OrderedUnsafe() => pending.OrderByDescending(item => item.Candidate.Kind switch
    {
        InteractionKind.Follow => 3,
        InteractionKind.Keyword => 2,
        _ => 1
    });

    private bool EligibleUnsafe(InteractionCandidate candidate, InteractionChannel channel)
    {
        if (!CurrentUnsafe(candidate) || !FreshUnsafe(candidate)) return false;
        var rule = candidate.RuleId is null ? null : rules.First(item => item.RuleId == candidate.RuleId);
        if (channel == InteractionChannel.Voice)
        {
            if (!candidate.VoiceEnabled) return false;
            if (voiceCompletedAt.TryGetValue(candidate.Kind, out var categoryEnd) && !Elapsed(categoryEnd, VoiceCooldown(candidate.Kind))) return false;
            return candidate.RuleId is null || !ruleVoiceCompletedAt.TryGetValue(candidate.RuleId, out var ruleEnd) || Elapsed(ruleEnd, rule!.Cooldown ?? TimeSpan.Zero);
        }
        if (channel != InteractionChannel.Text || !candidate.TextEnabled) return false;
        if (textCompletedAt is { } textEnd && !Elapsed(textEnd, options.TextCooldown)) return false;
        return candidate.RuleId is null || !ruleTextCompletedAt.TryGetValue(candidate.RuleId, out var completed) || Elapsed(completed, rule!.TextCooldown);
    }

    private void RemoveExpiredUnsafe()
    {
        foreach (var item in pending.ToArray())
            if (!item.VoiceStarted && !FreshUnsafe(item)) DiscardUnsafe(item, "Expired");
    }

    private void DiscardUnsafe(Pending item, string reason)
    {
        if (!pending.Remove(item)) return;
        if (ReferenceEquals(voiceLease, item)) voiceLease = null;
        if (ReferenceEquals(textLease, item)) textLease = null;
        // Admission reserves one notification slot for every live candidate, so no removal can lose its notification.
        discards.Enqueue(new(item.Candidate, reason));
    }

    private void RemoveFinishedUnsafe(Pending item)
    {
        if (!item.VoicePending && !item.TextPending && !ReferenceEquals(voiceLease, item) && !ReferenceEquals(textLease, item))
            pending.Remove(item);
    }

    private Pending? FindUnsafe(InteractionCandidate candidate) => pending.FirstOrDefault(item => ReferenceEquals(item.Candidate, candidate));
    private bool CurrentUnsafe(InteractionCandidate candidate) => !stopped && candidate.SessionId == sessionId && candidate.RoomId == roomId;
    private bool FreshUnsafe(InteractionCandidate candidate) => FindUnsafe(candidate) is { } item && FreshUnsafe(item);
    private bool FreshUnsafe(Pending item) => clock.Now.Ticks - item.AdmittedAt < item.RemainingTime.Ticks;
    private bool Elapsed(long at, TimeSpan duration) => clock.Now.Ticks - at >= duration.Ticks;
    private TimeSpan VoiceCooldown(InteractionKind kind) => kind switch
    {
        InteractionKind.Welcome => options.WelcomeCooldown,
        InteractionKind.Follow => options.FollowCooldown,
        _ => options.KeywordCooldown
    };
    private TimeSpan TimeToLive(InteractionKind kind) => kind switch
    {
        InteractionKind.Welcome => options.WelcomeTtl,
        InteractionKind.Follow => options.FollowTtl,
        _ => options.KeywordTtl
    };

    private string ChooseTemplate(InteractionKind kind, KeywordRule? rule, InteractionChannel channel)
    {
        var choices = (channel == InteractionChannel.Voice ? rule?.VoiceTemplates : rule?.TextTemplates) ?? rule?.Templates ?? (kind switch
        {
            InteractionKind.Welcome => options.WelcomeTemplates,
            InteractionKind.Follow => options.FollowTemplates,
            _ => options.KeywordTemplates
        });
        if (choices is { Count: > 0 }) return choices.Count == 1 ? choices[0] : choices[random!.NextInt32(0, choices.Count)];
        return rule?.Template ?? (kind switch
        {
            InteractionKind.Welcome => options.WelcomeTemplate,
            InteractionKind.Follow => options.FollowTemplate,
            _ => options.KeywordTemplate
        });
    }

    private void ValidateTemplates(IReadOnlyList<string>? templates)
    {
        if (templates is null) return;
        if (templates.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("候选模板不能为空。");
        if (templates.Count > 1 && random is null) throw new ArgumentException("多模板选择需要注入随机源。", nameof(random));
    }

    private static IReadOnlyList<string>? CopyTemplates(IReadOnlyList<string>? templates) => templates is null ? null : Array.AsReadOnly(templates.ToArray());
    private static InteractionAdmissionResult Invalid(string detail) => new(InteractionAdmissionStatus.Invalid, Detail: detail);
    private static bool Matches(KeywordRule rule, string content)
    {
        var matches = rule.Keywords.Select(InteractionTextSanitizer.MatchText).Select(keyword =>
            rule.MatchMode == KeywordMatchMode.Exact
                ? string.Equals(content, keyword, StringComparison.OrdinalIgnoreCase)
                : content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return rule.Requirement == KeywordMatchRequirement.All ? matches.All(value => value) : matches.Any(value => value);
    }

    private sealed class Pending(InteractionCandidate candidate, long admittedAt, TimeSpan remainingTime, DateTimeOffset admittedUtc)
    {
        public InteractionCandidate Candidate { get; } = candidate;
        public long AdmittedAt { get; } = admittedAt;
        public TimeSpan RemainingTime { get; } = remainingTime;
        public DateTimeOffset ExpiresAt { get; } = admittedUtc + remainingTime;
        public bool VoicePending { get; set; } = candidate.VoiceEnabled;
        public bool TextPending { get; set; } = candidate.TextEnabled;
        public bool VoiceStarted { get; set; }
    }
}
