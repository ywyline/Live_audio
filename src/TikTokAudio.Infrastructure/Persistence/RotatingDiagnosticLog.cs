using System.Text;
using System.Text.Json;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Persistence;

public enum DiagnosticEventCode
{
    StorageOpened,
    StorageWriteCompleted,
    StorageMigrationCompleted,
    ControlActionCompleted,
    OperationCancelled,
    OperationFailed
}

public sealed record DiagnosticEntry(
    DateTimeOffset TimestampUtc,
    DiagnosticEventCode Code,
    OperationStatus Status,
    Guid? SessionId = null,
    Guid? OperationId = null);

/// <summary>One writer per instance, with no free-form text or exception payload accepted.</summary>
public sealed class RotatingDiagnosticLog
{
    private const int MaximumRetainedFiles = 1000;
    private const string ActiveName = "diagnostic.jsonl";
    private readonly LocalDataPaths _paths;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly SemaphoreSlim _writer = new(1, 1);

    public RotatingDiagnosticLog(LocalDataPaths paths, long maxBytes, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFiles, MaximumRetainedFiles);
        _paths = paths;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
    }

    public async Task<OperationStatus> WriteAsync(DiagnosticEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enum.IsDefined(entry.Code) || !Enum.IsDefined(entry.Status))
            throw new ArgumentException("Diagnostic event codes and statuses must be defined.", nameof(entry));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry with
        {
            TimestampUtc = entry.TimestampUtc.ToUniversalTime()
        }) + "\n");
        if (bytes.LongLength > _maxBytes) return OperationStatus.Failed;

        var entered = false;
        try
        {
            entered = await _writer.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return OperationStatus.Failed;
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _paths.EnsureDirectories();
                using var ownership = AcquireOwnership();
                var active = LogPath(ActiveName);
                var length = File.Exists(active) ? new FileInfo(active).Length : 0;
                PruneArchives();
                if (length > _maxBytes) File.Delete(_paths.ValidatePath(active));
                else if (length > _maxBytes - bytes.Length) Rotate();
                cancellationToken.ThrowIfCancellationRequested();
                _paths.ValidatePath(active);
                await using var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return OperationStatus.Succeeded;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OperationStatus.Cancelled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationStatus.Failed;
        }
        finally
        {
            if (entered) _writer.Release();
        }
    }

    public async Task<OperationStatus> ClearAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            entered = await _writer.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return OperationStatus.Failed;
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _paths.ValidatePath(_paths.LogDirectory);
                if (!Directory.Exists(_paths.LogDirectory)) return OperationStatus.Succeeded;
                using var ownership = AcquireOwnership();
                var owned = OwnedFiles();
                foreach (var path in owned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _paths.ValidatePath(path);
                    File.Delete(path);
                }
                return OperationStatus.Succeeded;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OperationStatus.Cancelled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationStatus.Failed;
        }
        finally
        {
            if (entered) _writer.Release();
        }
    }

    private void PruneArchives()
    {
        foreach (var path in OwnedFiles())
        {
            var name = Path.GetFileName(path);
            if (name != ActiveName && (ArchiveIndex(name) >= _maxFiles || new FileInfo(path).Length > _maxBytes))
            {
                _paths.ValidatePath(path);
                File.Delete(path);
            }
        }
    }

    private void Rotate()
    {
        // Resolve and validate all fixed targets before the first rotation mutation.
        var paths = Enumerable.Range(0, _maxFiles)
            .Select(index => LogPath(index == 0 ? ActiveName : ArchiveName(index))).ToArray();
        File.Delete(paths[^1]);
        for (var index = paths.Length - 1; index > 0; index--)
        {
            _paths.ValidatePath(paths[index - 1]);
            _paths.ValidatePath(paths[index]);
            if (File.Exists(paths[index - 1])) File.Move(paths[index - 1], paths[index]);
        }
    }

    private string[] OwnedFiles()
    {
        _paths.ValidatePath(_paths.LogDirectory);
        return Directory.EnumerateFiles(_paths.LogDirectory, "diagnostic*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) == ActiveName || ArchiveIndex(Path.GetFileName(path)) is > 0 and < MaximumRetainedFiles)
            .Select(_paths.ValidatePath).ToArray();
    }

    private string LogPath(string name) => _paths.ValidatePath(Path.Combine(_paths.LogDirectory, name));
    private FileStream AcquireOwnership() => new(LogPath("diagnostic.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    private static string ArchiveName(int index) => $"diagnostic-{index:D4}.jsonl";
    private static int ArchiveIndex(string name)
    {
        if (name.Length != 21 || !name.StartsWith("diagnostic-", StringComparison.Ordinal) ||
            !name.EndsWith(".jsonl", StringComparison.Ordinal)) return -1;
        return int.TryParse(name.AsSpan(11, 4), out var index) && name == ArchiveName(index) ? index : -1;
    }
}
