using System.Text.Json;
using System.Runtime.InteropServices;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T080LocalDataTests
{
    [Fact]
    public void DevelopmentAndUserDataPathsAreSeparateAndResolutionDoesNotCreateDirectories()
    {
        using var temporary = new TemporaryData();
        var development = LocalDataPaths.ForDevelopment(temporary.Root);
        var user = LocalDataPaths.ForUser(temporary.Root, "SyntheticProduct");

        Assert.Equal(Path.Combine(temporary.Root, ".local-data"), development.RootDirectory);
        Assert.Equal(Path.Combine(temporary.Root, "SyntheticProduct"), user.RootDirectory);
        Assert.NotEqual(development.RootDirectory, user.RootDirectory);
        Assert.Equal(Path.Combine(user.RootDirectory, "state.sqlite3"), user.DatabasePath);
        Assert.False(Directory.Exists(development.RootDirectory));
        Assert.False(Directory.Exists(user.RootDirectory));
    }

    [Fact]
    public void EnsureDirectoriesOnlyCreatesSelectedDirectoriesAndIsIdempotent()
    {
        using var temporary = new TemporaryData();
        var paths = temporary.Paths;

        paths.EnsureDirectories();
        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.False(File.Exists(paths.DatabasePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("one/two")]
    [InlineData("one\\two")]
    [InlineData("name:stream")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("LPT1")]
    [InlineData("COM¹")]
    [InlineData("LPT²")]
    [InlineData("product.")]
    [InlineData("product ")]
    public void ProductNamesCannotEscapeOrUseAmbiguousDevicePaths(string? name)
    {
        using var temporary = new TemporaryData();

        Assert.ThrowsAny<ArgumentException>(() => LocalDataPaths.ForUser(temporary.Root, name!));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("../relative")]
    public void RootsMustBeExplicitAbsolutePaths(string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => LocalDataPaths.ForDevelopment(root));
        Assert.ThrowsAny<ArgumentException>(() => LocalDataPaths.ForUser(root, "Product"));
    }

    [Fact]
    public void CanonicalRootAndDescendantFilesAreAllowed()
    {
        using var temporary = new TemporaryData();
        temporary.Paths.EnsureDirectories();
        var path = Path.Combine(temporary.Paths.CacheDirectory, "synthetic.wav");
        File.WriteAllText(path, "synthetic");

        Assert.Equal(temporary.Paths.RootDirectory, temporary.Paths.ValidatePath(temporary.Paths.RootDirectory));
        Assert.Equal(path, temporary.Paths.ValidatePath(path));
    }

    [Fact]
    public void TraversalSiblingPrefixAndAlternateStreamsAreRejected()
    {
        using var temporary = new TemporaryData();
        var paths = temporary.Paths;

        Assert.ThrowsAny<ArgumentException>(() => paths.ValidatePath(Path.Combine(paths.CacheDirectory, "..", "inside-but-not-canonical")));
        Assert.ThrowsAny<ArgumentException>(() => paths.ValidatePath(Path.Combine(paths.RootDirectory + "-sibling", "file")));
        Assert.ThrowsAny<ArgumentException>(() => paths.ValidatePath(temporary.Root));
        Assert.ThrowsAny<ArgumentException>(() => paths.ValidatePath(Path.Combine(paths.CacheDirectory, "file:stream")));
        Assert.ThrowsAny<ArgumentException>(() => paths.ValidatePath("relative"));
    }

    [Fact]
    public void DirectoryCreationFailureDoesNotFallBackElsewhere()
    {
        using var temporary = new TemporaryData();
        File.WriteAllText(temporary.Paths.RootDirectory, "synthetic obstruction");

        Assert.ThrowsAny<IOException>(() => temporary.Paths.EnsureDirectories());
        Assert.True(File.Exists(temporary.Paths.RootDirectory));
        Assert.False(Directory.Exists(temporary.Paths.CacheDirectory));
    }

    [Fact]
    public void WindowsHardLinkedCacheFilesAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryData();
        temporary.Paths.EnsureDirectories();
        var source = Path.Combine(temporary.Root, "synthetic-source.txt");
        var linked = Path.Combine(temporary.Paths.CacheDirectory, "synthetic-link.txt");
        File.WriteAllText(source, "keep");
        Assert.True(CreateHardLink(linked, source, IntPtr.Zero));

        Assert.Throws<IOException>(() => temporary.Paths.ValidatePath(linked));
        Assert.Equal("keep", File.ReadAllText(source));
    }

    [Fact]
    public async Task WindowsHardLinkedActiveLogCannotBeWrittenOrCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryData();
        temporary.Paths.EnsureDirectories();
        var source = Path.Combine(temporary.Root, "synthetic-source.txt");
        var linked = Path.Combine(temporary.Paths.LogDirectory, "diagnostic.jsonl");
        await File.WriteAllTextAsync(source, "keep");
        Assert.True(CreateHardLink(linked, source, IntPtr.Zero));
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);

        Assert.Equal(OperationStatus.Failed, await log.WriteAsync(Entry(1)));
        Assert.Equal(OperationStatus.Failed, await log.ClearAsync());
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.True(File.Exists(linked));
    }

    [Fact]
    public async Task LogWritesOnlyStructuredFieldsAndNormalizesUtc()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);
        var entry = Entry(1) with { TimestampUtc = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(8)) };

        Assert.Equal(OperationStatus.Succeeded, await log.WriteAsync(entry));
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(temporary.Paths.LogDirectory, "diagnostic.jsonl")));
        using var json = JsonDocument.Parse(line);
        Assert.Equal(new[] { "TimestampUtc", "Code", "Status", "SessionId", "OperationId" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("TimestampUtc").GetDateTimeOffset().Offset);
        Assert.Equal(entry.OperationId, json.RootElement.GetProperty("OperationId").GetGuid());
    }

    [Fact]
    public async Task LogRotationBoundsEachFileAndRetainedCount()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 256, 3);
        for (var index = 0; index < 12; index++)
            Assert.Equal(OperationStatus.Succeeded, await log.WriteAsync(Entry(index)));

        var files = Directory.GetFiles(temporary.Paths.LogDirectory, "*.jsonl");
        Assert.Equal(3, files.Length);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 256));
        using var latest = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(
            Path.Combine(temporary.Paths.LogDirectory, "diagnostic.jsonl"))));
        Assert.Equal(Entry(11).OperationId, latest.RootElement.GetProperty("OperationId").GetGuid());
    }

    [Fact]
    public async Task SingleFileRetentionDoesNotKeepArchives()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 256, 1);
        for (var index = 0; index < 5; index++)
            Assert.Equal(OperationStatus.Succeeded, await log.WriteAsync(Entry(index)));

        Assert.Single(Directory.GetFiles(temporary.Paths.LogDirectory, "*.jsonl"));
    }

    [Fact]
    public async Task OversizedEntryIsRejectedWithoutCreatingLogDirectories()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1, 1);

        Assert.Equal(OperationStatus.Failed, await log.WriteAsync(Entry(1)));
        Assert.False(Directory.Exists(temporary.Paths.LogDirectory));
    }

    [Fact]
    public async Task LogCleanupPreservesUnrelatedAndLookalikeFiles()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 256, 3);
        for (var index = 0; index < 4; index++) await log.WriteAsync(Entry(index));
        string[] names = ["operator-notes.txt", "diagnostic-extra.jsonl", "diagnostic-0000.jsonl", "diagnostic-1000.jsonl"];
        foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(temporary.Paths.LogDirectory, name), "keep");

        Assert.Equal(OperationStatus.Succeeded, await log.ClearAsync());

        foreach (var name in names) Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(temporary.Paths.LogDirectory, name)));
        Assert.False(File.Exists(Path.Combine(temporary.Paths.LogDirectory, "diagnostic.jsonl")));
        Assert.False(File.Exists(Path.Combine(temporary.Paths.LogDirectory, "diagnostic-0001.jsonl")));
        Assert.False(File.Exists(Path.Combine(temporary.Paths.LogDirectory, "diagnostic-0002.jsonl")));
    }

    [Fact]
    public async Task TightenedRetentionPrunesOnlyOldOwnedArchives()
    {
        using var temporary = new TemporaryData();
        var original = new RotatingDiagnosticLog(temporary.Paths, 256, 5);
        for (var index = 0; index < 6; index++) await original.WriteAsync(Entry(index));
        var smaller = new RotatingDiagnosticLog(temporary.Paths, 256, 2);

        Assert.Equal(OperationStatus.Succeeded, await smaller.WriteAsync(Entry(10)));

        Assert.Equal(2, Directory.GetFiles(temporary.Paths.LogDirectory, "*.jsonl").Length);
    }

    [Fact]
    public async Task LogCrossInstanceOwnershipIsExclusiveAndDoesNotWait()
    {
        using var temporary = new TemporaryData();
        temporary.Paths.EnsureDirectories();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);
        using (var held = new FileStream(Path.Combine(temporary.Paths.LogDirectory, "diagnostic.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(OperationStatus.Failed, await log.WriteAsync(Entry(1)));
            Assert.Equal(OperationStatus.Failed, await log.ClearAsync());
        }

        Assert.Equal(OperationStatus.Succeeded, await log.WriteAsync(Entry(2)));
    }

    [Fact]
    public async Task CancelledLogOperationsDoNotCreateOrDeleteFiles()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(OperationStatus.Cancelled, await log.WriteAsync(Entry(1), cancellation.Token));
        Assert.False(Directory.Exists(temporary.Paths.LogDirectory));
        await log.WriteAsync(Entry(2));
        Assert.Equal(OperationStatus.Cancelled, await log.ClearAsync(cancellation.Token));
        Assert.True(File.Exists(Path.Combine(temporary.Paths.LogDirectory, "diagnostic.jsonl")));
    }

    [Fact]
    public async Task ClearingAbsentLogsIsAnIdempotentNoOp()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);

        Assert.Equal(OperationStatus.Succeeded, await log.ClearAsync());
        Assert.Equal(OperationStatus.Succeeded, await log.ClearAsync());
        Assert.False(Directory.Exists(temporary.Paths.LogDirectory));
    }

    [Fact]
    public async Task UnknownDiagnosticEnumsAreRejected()
    {
        using var temporary = new TemporaryData();
        var log = new RotatingDiagnosticLog(temporary.Paths, 1024, 2);

        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(Entry(1) with { Code = (DiagnosticEventCode)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(Entry(1) with { Status = (OperationStatus)999 }));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1024, 0)]
    [InlineData(1024, 1001)]
    public void LogLimitsAreExplicitAndValidated(long bytes, int files)
    {
        using var temporary = new TemporaryData();

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new RotatingDiagnosticLog(temporary.Paths, bytes, files));
    }

    [Fact]
    public async Task FakeCredentialStoreRoundTripsUnicodeAndDeletesWithinItsNamespace()
    {
        var api = new FakeCredentialApi();
        var store = new WindowsCredentialStore("SyntheticProduct", api);
        using var secret = new CredentialSecret("synthetic-tiếng-Việt");

        Assert.Equal(CredentialStatus.Succeeded, await store.WriteAsync("session", secret));
        var read = await store.ReadAsync("session");
        using (read.Secret)
        {
            Assert.Equal(CredentialStatus.Succeeded, read.Status);
            Assert.Equal(secret.Reveal(), read.Secret!.Reveal());
        }
        Assert.Equal("LiveAudio:SyntheticProduct:session", api.LastTarget);
        Assert.Equal(CredentialStatus.Succeeded, await store.DeleteAsync("session"));
        Assert.Equal(CredentialStatus.NotFound, (await store.ReadAsync("session")).Status);
        Assert.Equal(CredentialStatus.NotFound, await store.DeleteAsync("session"));
    }

    [Fact]
    public async Task ProductNamespacesCannotReadOrDeleteEachOthersCredentials()
    {
        var api = new FakeCredentialApi();
        var first = new WindowsCredentialStore("FirstProduct", api);
        var second = new WindowsCredentialStore("SecondProduct", api);
        using var secret = new CredentialSecret("synthetic-only");
        await first.WriteAsync("token", secret);

        Assert.Equal(CredentialStatus.NotFound, (await second.ReadAsync("token")).Status);
        Assert.Equal(CredentialStatus.NotFound, await second.DeleteAsync("token"));
        var original = await first.ReadAsync("token");
        using (original.Secret) Assert.Equal(CredentialStatus.Succeeded, original.Status);
    }

    [Fact]
    public async Task UnsupportedCredentialApiNeverReceivesNativeCalls()
    {
        var api = new FakeCredentialApi { IsSupported = false };
        var store = new WindowsCredentialStore("Product", api);
        using var secret = new CredentialSecret("synthetic");

        Assert.Equal(CredentialStatus.Unsupported, await store.WriteAsync("token", secret));
        Assert.Equal(CredentialStatus.Unsupported, (await store.ReadAsync("token")).Status);
        Assert.Equal(CredentialStatus.Unsupported, await store.DeleteAsync("token"));
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task CancelledCredentialOperationsDoNotInvokeTheApi()
    {
        var api = new FakeCredentialApi();
        var store = new WindowsCredentialStore("Product", api);
        using var secret = new CredentialSecret("synthetic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(CredentialStatus.Cancelled, await store.WriteAsync("token", secret, cancellation.Token));
        Assert.Equal(CredentialStatus.Cancelled, (await store.ReadAsync("token", cancellation.Token)).Status);
        Assert.Equal(CredentialStatus.Cancelled, await store.DeleteAsync("token", cancellation.Token));
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task CredentialErrorsNeverReturnRawExceptionText()
    {
        var api = new FakeCredentialApi { Throw = true };
        var store = new WindowsCredentialStore("Product", api);
        using var secret = new CredentialSecret("synthetic-sensitive-secret");

        Assert.Equal(CredentialStatus.Failed, await store.WriteAsync("token", secret));
        var read = await store.ReadAsync("token");
        Assert.Equal(CredentialStatus.Failed, read.Status);
        Assert.Null(read.Secret);
        Assert.DoesNotContain("synthetic-sensitive-secret", read.ToString());
        Assert.Equal(CredentialStatus.Failed, await store.DeleteAsync("token"));
    }

    [Fact]
    public async Task BusyCredentialStoreRejectsWithoutQueuingAnotherOperation()
    {
        var api = new FakeCredentialApi();
        var store = new WindowsCredentialStore("Product", api);
        Task<CredentialStatus>? nested = null;
        api.OnWrite = () => nested = store.DeleteAsync("other");
        using var secret = new CredentialSecret("synthetic");

        Assert.Equal(CredentialStatus.Succeeded, await store.WriteAsync("token", secret));
        Assert.NotNull(nested);
        Assert.Equal(CredentialStatus.Failed, await nested);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task CredentialNamesRejectNamespaceEscapeBeforeAnyApiCall()
    {
        var api = new FakeCredentialApi();
        var store = new WindowsCredentialStore("Product", api);
        using var secret = new CredentialSecret("synthetic");

        Assert.ThrowsAny<ArgumentException>(() => new WindowsCredentialStore("../other", api));
        Assert.ThrowsAny<ArgumentException>(() => { _ = store.WriteAsync("other:token", secret); });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync("../other"));
        Assert.ThrowsAny<ArgumentException>(() => { _ = store.DeleteAsync(new string('x', 129)); });
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public void SecretFormattingIsRedactedAndDisposalDisablesAccess()
    {
        var secret = new CredentialSecret("synthetic-sensitive-secret");
        var read = new CredentialReadResult(CredentialStatus.Succeeded, secret);

        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.DoesNotContain("synthetic-sensitive-secret", read.ToString());
        Assert.DoesNotContain("synthetic-sensitive-secret", JsonSerializer.Serialize(read));
        secret.Dispose();
        secret.Dispose();
        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.Throws<ObjectDisposedException>(() => secret.Reveal());
    }

    [Fact]
    public void SecretSizeUsesUtf8BytesAndRejectsMalformedUtf16()
    {
        using var maximum = new CredentialSecret(new string('a', 2560));

        Assert.ThrowsAny<ArgumentException>(() => new CredentialSecret(new string('a', 2561)));
        Assert.ThrowsAny<ArgumentException>(() => new CredentialSecret(new string('ế', 854)));
        Assert.ThrowsAny<ArgumentException>(() => new CredentialSecret("bad\ud800text"));
        Assert.ThrowsAny<ArgumentException>(() => new CredentialSecret(string.Empty));
    }

    [Fact]
    public void ReadResultsCannotPairASecretWithFailureOrSuccessWithoutASecret()
    {
        using var secret = new CredentialSecret("synthetic");

        Assert.Throws<ArgumentException>(() => new CredentialReadResult(CredentialStatus.Succeeded));
        Assert.Throws<ArgumentException>(() => new CredentialReadResult(CredentialStatus.Failed, secret));
        Assert.Throws<ArgumentException>(() => new CredentialReadResult((CredentialStatus)999));
    }

    private static DiagnosticEntry Entry(int index) => new(DateTimeOffset.UnixEpoch.AddSeconds(index),
        DiagnosticEventCode.StorageWriteCompleted, OperationStatus.Succeeded,
        Guid.Parse("391e354c-b279-414d-bdf7-c6418998b0c7"), new Guid(index, 0, 0, new byte[8]));

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class FakeCredentialApi : IWindowsCredentialApi
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public bool IsSupported { get; init; } = true;
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public string? LastTarget { get; private set; }
        public Action? OnWrite { get; set; }

        public CredentialStatus Write(string targetName, CredentialSecret secret)
        {
            Observe(targetName);
            OnWrite?.Invoke();
            _values[targetName] = secret.Reveal();
            return CredentialStatus.Succeeded;
        }

        public CredentialReadResult Read(string targetName)
        {
            Observe(targetName);
            return _values.TryGetValue(targetName, out var secret)
                ? new CredentialReadResult(CredentialStatus.Succeeded, new CredentialSecret(secret))
                : new CredentialReadResult(CredentialStatus.NotFound);
        }

        public CredentialStatus Delete(string targetName)
        {
            Observe(targetName);
            return _values.Remove(targetName) ? CredentialStatus.Succeeded : CredentialStatus.NotFound;
        }

        private void Observe(string target)
        {
            Calls++;
            LastTarget = target;
            if (Throw) throw new InvalidOperationException("synthetic-sensitive-secret");
        }
    }

    private sealed class TemporaryData : IDisposable
    {
        public TemporaryData()
        {
            Root = Path.Combine(Path.GetTempPath(), "LiveAudio-T080-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = LocalDataPaths.ForUser(Root, "SyntheticProduct");
        }

        public string Root { get; }
        public LocalDataPaths Paths { get; }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (resolved != Root || Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(resolved).StartsWith("LiveAudio-T080-", StringComparison.Ordinal))
                throw new InvalidOperationException("The synthetic temporary directory is outside its expected parent.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
