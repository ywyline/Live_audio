using TikTokAudio.Domain;

namespace TikTokAudio.Application.Media;

public enum MediaImportIssueCode
{
    InvalidRoot,
    EmptyDirectory,
    InvalidNumber,
    DuplicateNumber,
    UnexpectedEntry,
    UnsafePath,
    UnsupportedFile,
    InvalidAudio,
    MissingMapping,
    InvalidMetadata,
    ResourceLimit,
    IoFailure
}

public sealed record MediaImportIssue(
    MediaImportIssueCode Code,
    string RelativePath,
    int? ProductNumber,
    string Detail);

public sealed record ImportedAudioClip(
    string ClipId,
    LocalAudioAsset Asset,
    int? ProductOverrideNumber);

public sealed record ImportedAudioGroup(
    int Number,
    string RelativePath,
    IReadOnlyList<ImportedAudioClip> Clips);

public sealed record ImportedAudioProduct(
    int Number,
    string RelativePath,
    string? PlatformProductId,
    IReadOnlyList<ImportedAudioGroup> Groups,
    bool CanStart);

// Local preflight only: presence of an explicit ID is not platform confirmation.
public sealed record ProductDirectoryImport(
    string RootDirectory,
    IReadOnlyList<ImportedAudioProduct> Products,
    IReadOnlyList<MediaImportIssue> Issues)
{
    public bool IsValid => Issues.Count == 0 && Products.Count > 0;
}

public sealed record ProductDirectoryImportOptions
{
    public required string RootDirectory { get; init; }
    public required string ValidationCacheDirectory { get; init; }
    public required long MaxInputBytes { get; init; }
    public required long MaxDecodedBytes { get; init; }
    public required int MaxEntries { get; init; }
}
