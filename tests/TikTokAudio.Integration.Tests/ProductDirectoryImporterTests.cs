using NAudio.Wave;
using TikTokAudio.Application.Media;
using TikTokAudio.Infrastructure.Media;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class ProductDirectoryImporterTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "LiveAudio-T051-tests-" + Guid.NewGuid().ToString("N"));
    private string Materials => Path.Combine(workspace, "materials");
    private string Cache => Path.Combine(workspace, "cache");
    private ProductDirectoryImportOptions Options => new()
    {
        RootDirectory = Materials,
        ValidationCacheDirectory = Cache,
        MaxInputBytes = 1_000_000,
        MaxDecodedBytes = 1_000_000,
        MaxEntries = 100
    };

    [Fact]
    public async Task ProductsAndGroupsUseNumericOrderAndAllowDifferentGroupCounts()
    {
        WriteWave("10/10/tenth.wav");
        WriteWave("10/2/second.wav");
        WriteWave("10/1/first.wav");
        WriteWave("2/7/only.wav");
        WriteWave("1/3/only.wav");

        var result = await Import(Mappings(1, 2, 10));

        Assert.True(result.IsValid);
        Assert.Equal(new[] { 1, 2, 10 }, result.Products.Select(product => product.Number));
        Assert.Equal(new[] { 1, 2, 10 }, result.Products[2].Groups.Select(group => group.Number));
        Assert.Single(result.Products[0].Groups);
        Assert.Single(result.Products[1].Groups);
        Assert.All(result.Products, product => Assert.True(product.CanStart));
    }

    [Fact]
    public async Task UniqueLeadingZeroNumbersAreAccepted()
    {
        WriteWave("01/002/voice.wav");
        var result = await Import(Mappings(1));
        Assert.True(result.IsValid);
        Assert.Equal(1, Assert.Single(result.Products).Number);
        Assert.Equal(2, Assert.Single(result.Products[0].Groups).Number);
    }

    [Fact]
    public async Task DuplicateProductNumbersBlockConflictingProductsButKeepIndependentProductReady()
    {
        WriteWave("01/1/one.wav");
        WriteWave("1/1/another.wav");
        WriteWave("2/1/good.wav");
        var result = await Import(Mappings(1, 2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.DuplicateNumber);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Products.Count(product => product.Number == 1));
        Assert.All(result.Products.Where(product => product.Number == 1), product => Assert.False(product.CanStart));
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
    }

    [Fact]
    public async Task DuplicateGroupNumbersBlockOnlyTheirProduct()
    {
        WriteWave("1/01/one.wav");
        WriteWave("1/1/another.wav");
        WriteWave("2/1/good.wav");
        var result = await Import(Mappings(1, 2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.DuplicateNumber && issue.ProductNumber == 1);
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    [InlineData("١")]
    [InlineData("１")]
    [InlineData("goods")]
    public async Task ProductNumbersMustBePositiveAsciiIntegers(string name)
    {
        WriteWave(name + "/1/voice.wav");
        WriteWave("2/1/good.wav");
        var result = await Import(Mappings(2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidNumber);
        Assert.All(result.Products, product => Assert.False(product.CanStart));
    }

    [Fact]
    public async Task InvalidGroupNumberBlocksOnlyOwningProduct()
    {
        WriteWave("1/zero/voice.wav");
        WriteWave("2/1/good.wav");
        var result = await Import(Mappings(1, 2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidNumber && issue.ProductNumber == 1);
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1/1")]
    public async Task EmptyRootProductOrGroupIsReported(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(Materials, relativePath));
        var result = await Import(Mappings(1));
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.EmptyDirectory);
        Assert.All(result.Products, product => Assert.False(product.CanStart));
    }

    [Fact]
    public async Task PreflightCollectsBadFilesAndEmptyGroupsWhileKeepingIndependentProductReady()
    {
        WriteFile("1/1/broken.wav", new byte[] { 1, 2, 3 });
        WriteFile("1/1/script.txt", new byte[] { 4, 5 });
        Directory.CreateDirectory(Path.Combine(Materials, "1", "2"));
        WriteWave("2/1/good.wav");
        var result = await Import(Mappings(1, 2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidAudio && issue.ProductNumber == 1);
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.UnsupportedFile && issue.ProductNumber == 1);
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.EmptyDirectory && issue.ProductNumber == 1);
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
        AssertNoOwnedSessions();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOrBlankMappingBlocksOnlyAffectedProduct(string? missingMapping)
    {
        WriteWave("1/1/one.wav");
        WriteWave("2/1/two.wav");
        var mappings = new Dictionary<int, string> { [2] = "synthetic-product-2" };
        if (missingMapping is not null) mappings[1] = missingMapping;
        var result = await Import(mappings);
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.MissingMapping && issue.ProductNumber == 1);
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
    }

    [Fact]
    public async Task ExplicitClipOverrideIsStoredWithoutInterpretingFilenameMarkers()
    {
        string path = WriteWave("1/1/@[999]-tiếng Việt.wav");
        WriteWave("2/1/two.wav");
        var metadata = new Dictionary<string, int> { ["1/1/@[999]-tiếng Việt.wav"] = 2 };
        var result = await Import(Mappings(1, 2), metadata);
        Assert.True(result.IsValid);
        var clip = Assert.Single(result.Products.Single(product => product.Number == 1).Groups[0].Clips);
        Assert.Equal(2, clip.ProductOverrideNumber);
        Assert.Equal(path, clip.Asset.Path);
        metadata["1/1/@[999]-tiếng Việt.wav"] = 100;
        Assert.Equal(2, clip.ProductOverrideNumber);
    }

    [Fact]
    public async Task FilenameMarkerAloneNeverCreatesProductOverride()
    {
        WriteWave("1/1/@[999].wav");
        var result = await Import(Mappings(1));
        Assert.True(result.IsValid);
        Assert.Null(Assert.Single(result.Products[0].Groups[0].Clips).ProductOverrideNumber);
    }

    [Theory]
    [InlineData("1/1/voice.wav", 0)]
    [InlineData("1/1/voice.wav", -1)]
    [InlineData("1/1/absent.wav", 1)]
    [InlineData("../outside.wav", 1)]
    [InlineData("", 1)]
    public async Task InvalidOverrideMetadataIsReported(string key, int target)
    {
        WriteWave("1/1/voice.wav");
        var result = await Import(Mappings(1), new Dictionary<string, int> { [key] = target });
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidMetadata);
        Assert.False(Assert.Single(result.Products).CanStart);
    }

    [Fact]
    public async Task OverrideTargetRequiresAnExplicitMapping()
    {
        WriteWave("1/1/voice.wav");
        var result = await Import(Mappings(1), new Dictionary<string, int> { ["1/1/voice.wav"] = 9 });
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.MissingMapping);
        Assert.False(Assert.Single(result.Products).CanStart);
    }

    [Fact]
    public async Task NestedFoldersAreReportedWithoutRecursivelyImportingTheirFiles()
    {
        WriteWave("1/1/direct.wav");
        string nested = WriteWave("1/1/nested/hidden.wav");
        string outside = Path.Combine(workspace, "unrelated", "3", "1", "outside.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "outside is not valid audio");
        var result = await Import(Mappings(1));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.UnexpectedEntry);
        Assert.DoesNotContain(result.Products.SelectMany(product => product.Groups).SelectMany(group => group.Clips), clip => clip.Asset.Path == nested);
        Assert.DoesNotContain(result.Issues, issue => issue.RelativePath.Contains("outside", StringComparison.Ordinal));
        Assert.Single(result.Products);
    }

    [Fact]
    public async Task RootFilesAreGlobalErrorsAndAreNeverTreatedAsProducts()
    {
        WriteWave("1/1/good.wav");
        WriteFile("readme.txt", new byte[] { 1 });
        var result = await Import(Mappings(1));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.UnexpectedEntry && issue.ProductNumber is null);
        Assert.False(Assert.Single(result.Products).CanStart);
    }

    [Fact]
    public async Task MissingRootReturnsDiagnosticInsteadOfCreatingIt()
    {
        var result = await Import(Mappings(1));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidRoot);
        Assert.False(result.IsValid);
        Assert.False(Directory.Exists(Materials));
    }

    [Fact]
    public async Task AlreadyCancelledImportThrowsWithoutCreatingValidationSessions()
    {
        WriteWave("1/1/voice.wav");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProductDirectoryImporter(Options)
            .ImportAsync(Mappings(1), cancellationToken: cancellation.Token));
        AssertNoOwnedSessions();
    }

    [Fact]
    public async Task EnumerationLimitIsGlobalAndIncludesDirectoriesAndFiles()
    {
        WriteWave("1/1/a.wav");
        WriteWave("1/1/b.wav");
        var result = await new ProductDirectoryImporter(Options with { MaxEntries = 3 }).ImportAsync(Mappings(1));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.ResourceLimit && issue.ProductNumber is null);
        Assert.All(result.Products, product => Assert.False(product.CanStart));
        AssertNoOwnedSessions();
    }

    [Fact]
    public async Task EnumerationLimitAllowsExactlyTheConfiguredNumber()
    {
        WriteWave("1/1/a.wav");
        var result = await new ProductDirectoryImporter(Options with { MaxEntries = 3 }).ImportAsync(Mappings(1));
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ImportedAssetsReferenceUntouchedMaterialsAndOwnedCacheIsReleased()
    {
        string path = WriteWave("1/1/voice.WAV");
        byte[] original = File.ReadAllBytes(path);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        Directory.CreateDirectory(Cache);
        string unrelated = Path.Combine(Cache, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        var result = await Import(Mappings(1));
        Assert.True(result.IsValid);
        var asset = Assert.Single(result.Products[0].Groups[0].Clips).Asset;
        Assert.Equal(path, asset.Path);
        Assert.Equal("prerecorded", asset.EngineId);
        Assert.Equal(8000, asset.SampleRate);
        Assert.Equal(TimeSpan.FromMilliseconds(10), asset.Duration);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        AssertNoOwnedSessions();
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\materials")]
    public void NonLocalOrRelativeRootIsRejectedBeforeEnumeration(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProductDirectoryImporter(Options with { RootDirectory = input }));
    }

    [Fact]
    public void ExplicitTraversalAndOverlappingCacheAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProductDirectoryImporter(Options with
        {
            RootDirectory = Path.Combine(Materials, "sub", "..")
        }));
        Assert.ThrowsAny<ArgumentException>(() => new ProductDirectoryImporter(Options with
        {
            ValidationCacheDirectory = Path.Combine(Materials, "cache")
        }));
    }

    [Theory]
    [InlineData(0, 1000, 10)]
    [InlineData(1000, 0, 10)]
    [InlineData(1000, 1000, 0)]
    public void InvalidResourceLimitsAreRejected(long input, long decoded, int entries)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProductDirectoryImporter(Options with
        {
            MaxInputBytes = input, MaxDecodedBytes = decoded, MaxEntries = entries
        }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedMappingOrMetadataInputReturnsGlobalResourceLimitWithoutImporting(bool excessiveMappings)
    {
        WriteWave("1/1/voice.wav");
        var mappings = excessiveMappings ? Mappings(1, 2, 3, 4) : Mappings(1);
        var overrides = excessiveMappings ? null : new Dictionary<string, int>
        {
            ["1/1/a.wav"] = 1,
            ["1/1/b.wav"] = 1,
            ["1/1/c.wav"] = 1,
            ["1/1/d.wav"] = 1
        };
        var result = await new ProductDirectoryImporter(Options with { MaxEntries = 3 }).ImportAsync(mappings, overrides);
        Assert.False(result.IsValid);
        Assert.Empty(result.Products);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(MediaImportIssueCode.ResourceLimit, issue.Code);
        Assert.Null(issue.ProductNumber);
        AssertNoOwnedSessions();
    }

    [Fact]
    public async Task InputSizeLimitReportsBadProductAndKeepsIndependentGoodProductReady()
    {
        WriteWave("1/1/oversized.wav", 800);
        WriteWave("2/1/good.wav");
        var result = await new ProductDirectoryImporter(Options with { MaxInputBytes = 256 }).ImportAsync(Mappings(1, 2));
        Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidAudio &&
            issue.ProductNumber == 1 && issue.RelativePath == "1/1/oversized.wav");
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
        Assert.Single(result.Products.Single(product => product.Number == 2).Groups[0].Clips);
        AssertNoOwnedSessions();
    }

    [Fact]
    public async Task EmptyTruncatedAndExcessDecodedAudioAreCollectedAsInvalidAudio()
    {
        WriteWave("1/1/empty.wav", 0);
        string truncated = WriteWave("1/1/truncated.wav");
        using (var stream = new FileStream(truncated, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length - 10);
        WriteWave("1/1/excess-decoded.wav", 800);
        WriteWave("2/1/good.wav");
        var result = await new ProductDirectoryImporter(Options with { MaxDecodedBytes = 200 }).ImportAsync(Mappings(1, 2));
        foreach (string relative in new[] { "1/1/empty.wav", "1/1/truncated.wav", "1/1/excess-decoded.wav" })
            Assert.Contains(result.Issues, issue => issue.Code == MediaImportIssueCode.InvalidAudio &&
                issue.ProductNumber == 1 && issue.RelativePath == relative);
        Assert.False(result.Products.Single(product => product.Number == 1).CanStart);
        Assert.True(result.Products.Single(product => product.Number == 2).CanStart);
        AssertNoOwnedSessions();
    }
    private Task<ProductDirectoryImport> Import(IReadOnlyDictionary<int, string> mappings,
        IReadOnlyDictionary<string, int>? metadata = null) =>
        new ProductDirectoryImporter(Options).ImportAsync(mappings, metadata);

    private static Dictionary<int, string> Mappings(params int[] numbers) =>
        numbers.ToDictionary(number => number, number => "synthetic-product-" + number);

    private string WriteWave(string relativePath, int frames = 80)
    {
        string path = Path.Combine(Materials, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1));
        writer.Write(new byte[frames * 2], 0, frames * 2);
        return path;
    }

    private void WriteFile(string relativePath, byte[] content)
    {
        string path = Path.Combine(Materials, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void AssertNoOwnedSessions()
    {
        if (Directory.Exists(Cache)) Assert.Empty(Directory.EnumerateDirectories(Cache, "session-*"));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(workspace);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("LiveAudio-T051-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test directory cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
