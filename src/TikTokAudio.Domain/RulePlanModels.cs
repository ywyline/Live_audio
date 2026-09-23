namespace TikTokAudio.Domain;

public sealed record RuleSetSnapshot(string RuleSetId, int Version, DateTimeOffset UpdatedAtUtc);

public sealed record PlaybackPlanSnapshot(
    string PlanId,
    BasePlaybackMode Mode,
    PlanRevision Revision,
    DateTimeOffset UpdatedAtUtc);
