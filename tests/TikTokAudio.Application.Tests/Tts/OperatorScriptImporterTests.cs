using System.Text;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests.Tts;

public sealed class OperatorScriptImporterTests
{
    private static readonly IReadOnlyDictionary<int, string> Products = new Dictionary<int, string>
    {
        [1] = "product-one", [2] = "product-two", [3] = "product-three"
    };

    [Fact]
    public void PreservesOriginalAndNormalizesVietnameseWithoutRemovingAccents()
    {
        const string original = "  Ha\u0309i Đa\u0306ng xin cha\u0300o.  ";
        var result = Import(original);
        var document = AssertSuccess(result);
        Assert.Equal(original, document.OriginalText);
        Assert.Equal("Hải Đăng xin chào.", Assert.Single(document.Segments).Text);
        Assert.Equal(OperatorScriptImporter.Version, document.SegmenterVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsUtf8WithOptionalBom(bool withBom)
    {
        var bytes = Encoding.UTF8.GetBytes("Xin chào!");
        if (withBom) bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray();
        Assert.Equal("Xin chào!", Assert.Single(AssertSuccess(Create().Import(bytes, Products)).Segments).Text);
    }

    [Fact]
    public void RejectsMalformedUtf8AndDoesNotSubstituteReplacementCharacters()
    {
        AssertFailure(Create().Import(new byte[] { 0xc3, 0x28 }, Products));
        AssertFailure(Create().Import(new byte[] { 0xed, 0xa0, 0x80 }, Products));
        AssertFailure(Create().Import(new byte[] { 0xff }, Products));
    }

    [Fact]
    public void RejectsUtf16AndUtf32InsteadOfGuessingEncoding()
    {
        foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            var bytes = encoding.GetBytes("Xin chào");
            AssertFailure(Create().Import(bytes, Products));
            AssertFailure(Create().Import(encoding.GetPreamble().Concat(bytes).ToArray(), Products));
        }
    }

    [Theory]
    [InlineData("abc\0def")]
    [InlineData("abc\u0001def")]
    [InlineData("abc\u007fdef")]
    public void RejectsUnsupportedControlCharacters(string text) => AssertFailure(Import(text));

    [Fact]
    public void SplitsParagraphsSentencesAndKeepsDecimalNumbers()
    {
        var document = AssertSuccess(Import("Xin chào! Bạn khỏe không?\r\nGiá 3.50.\n\nCảm ơn。Tiếp theo…"));
        Assert.Equal(new[] { "Xin chào!", "Bạn khỏe không?", "Giá 3.50.", "Cảm ơn。", "Tiếp theo…" },
            document.Segments.Select(s => s.Text));
        Assert.Equal(Enumerable.Range(0, document.Segments.Count), document.Segments.Select(s => s.Index));
        Assert.All(document.Segments, s => Assert.Null(s.Boundary));
    }

    [Fact]
    public void KeepsTerminalPunctuationAndClosingQuotesTogether()
    {
        var document = AssertSuccess(Import("“Xin chào?!” Tiếp."));
        Assert.Equal(new[] { "“Xin chào?!”", "Tiếp." }, document.Segments.Select(s => s.Text));
    }

    [Fact]
    public void LengthSplitPrefersWordsAndNeverExceedsConfiguredLimit()
    {
        var document = AssertSuccess(Import("Xin chào bạn", maxLength: 8));
        Assert.Equal(new[] { "Xin", "chào bạn" }, document.Segments.Select(s => s.Text));
        Assert.All(document.Segments, s => Assert.InRange(s.Text.Length, 1, 8));
    }

    [Fact]
    public void SplitsUnbrokenLongTextAtConfiguredLength()
    {
        var document = AssertSuccess(Import("abcdefghij", maxLength: 3));
        Assert.Equal(new[] { "abc", "def", "ghi", "j" }, document.Segments.Select(s => s.Text));
    }

    [Fact]
    public void DoesNotSplitEmojiSurrogatesOrCombiningSequences()
    {
        const string emoji = "👨‍👩‍👧‍👦";
        var document = AssertSuccess(Import("A" + emoji + "q\u0307", maxLength: emoji.Length));
        Assert.Equal(new[] { "A", emoji, "q\u0307" }, document.Segments.Select(s => s.Text));
        AssertFailure(Import(emoji, maxLength: emoji.Length - 1));
    }

    [Fact]
    public void SentenceSplittingKeepsCombiningMarksAttachedToPunctuation()
    {
        var document = AssertSuccess(Import("A.\u0301 B"));
        Assert.Equal(new[] { "A.\u0301", "B" }, document.Segments.Select(s => s.Text));
    }

    [Fact]
    public void WhitespaceWithCombiningMarkIsNotTrimmedApart()
    {
        var document = AssertSuccess(Import(" \u0301A \u0301"));
        Assert.Equal(" \u0301A \u0301", Assert.Single(document.Segments).Text);
    }
    [Fact]
    public void ProductMarkerIsRemovedAndBoundaryOnlyAttachesToFirstFollowingSegment()
    {
        var document = AssertSuccess(Import("Trước. @[2] Sau! Tiếp tục."));
        Assert.Equal(new[] { "Trước.", "Sau!", "Tiếp tục." }, document.Segments.Select(s => s.Text));
        Assert.Null(document.Segments[0].Boundary);
        Assert.Equal("product-two", document.Segments[1].Boundary!.ProductId);
        Assert.Null(document.Segments[2].Boundary);
        Assert.All(document.Segments, segment => Assert.DoesNotContain("@[", segment.Text));
    }

    [Fact]
    public void MarkerCreatesBoundaryEvenWithoutWhitespaceOrPunctuation()
    {
        var document = AssertSuccess(Import("Trước@[1]Sau"));
        Assert.Equal(new[] { "Trước", "Sau" }, document.Segments.Select(s => s.Text));
        Assert.Null(document.Segments[0].Boundary);
        Assert.Equal("product-one", document.Segments[1].Boundary!.ProductId);
    }

    [Fact]
    public void ConsecutiveMarkersKeepOnlyLastTarget()
    {
        var document = AssertSuccess(Import("@[1]\r\n \t@[2] @[3]Xin chào. Tiếp."));
        Assert.Equal("product-three", document.Segments[0].Boundary!.ProductId);
        Assert.Null(document.Segments[1].Boundary);
    }

    [Fact]
    public void TrailingMarkerProducesWarningWithoutAStandaloneBoundary()
    {
        var result = Import("Xin chào. @[1] @[2] \n");
        var segment = Assert.Single(AssertSuccess(result).Segments);
        Assert.Null(segment.Boundary);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("@[1]")]
    public void RejectsScriptsWithoutPlayableText(string text) => AssertFailure(Import(text));

    [Theory]
    [InlineData("@[0]Hi")]
    [InlineData("@[-1]Hi")]
    [InlineData("@[+1]Hi")]
    [InlineData("@[1.0]Hi")]
    [InlineData("@[ 1]Hi")]
    [InlineData("@[1 ]Hi")]
    [InlineData("@[]Hi")]
    [InlineData("@[2147483648]Hi")]
    [InlineData("@[999999999999999999999]Hi")]
    [InlineData("@[１]Hi")]
    [InlineData("@[١]Hi")]
    [InlineData("@[abc]Hi")]
    [InlineData("@[1Hi")]
    [InlineData("@[[1]]Hi")]
    [InlineData("@[../secret]Hi")]
    [InlineData("@[C:\\audio.wav]Hi")]
    [InlineData("@[https://example.test]Hi")]
    [InlineData("@[1;run]Hi")]
    public void RejectsMalformedMarkersAndInjectedCommandsOrPaths(string text) => AssertFailure(Import(text));

    [Fact]
    public void UnknownMarkerFailsEvenIfFollowedByValidMarkerOrAtEnd()
    {
        AssertFailure(Import("@[99]@[1]Hi"));
        AssertFailure(Import("Hi @[99]"));
        AssertFailure(Create().Import(Encoding.UTF8.GetBytes("@[1]Hi"), new Dictionary<int, string> { [1] = " " }));
    }

    [Fact]
    public void OrdinaryAtSignsAndBracketsRemainSpeechText()
    {
        Assert.Equal("user@example [1]", Assert.Single(AssertSuccess(Import("user@example [1]")).Segments).Text);
    }

    [Fact]
    public void RepeatedMarkersHaveUniqueStableIdsPerDocumentPosition()
    {
        const string text = "@[1]Hi @[1]Hi @[2]Hi";
        var first = AssertSuccess(Import(text));
        var second = AssertSuccess(Import(text));
        var firstIds = first.Segments.Select(s => s.Boundary!.BoundaryId).ToArray();
        Assert.Equal(firstIds.Length, firstIds.Distinct().Count());
        Assert.Equal(firstIds, second.Segments.Select(s => s.Boundary!.BoundaryId));
        Assert.NotEqual(firstIds[0], AssertSuccess(Import("@[1]Changed")).Segments[0].Boundary!.BoundaryId);
    }

    [Fact]
    public void LengthSplittingDoesNotDuplicateProductBoundary()
    {
        var document = AssertSuccess(Import("@[1]abcdefgh", maxLength: 3));
        Assert.Equal(3, document.Segments.Count);
        Assert.Equal("product-one", document.Segments[0].Boundary!.ProductId);
        Assert.All(document.Segments.Skip(1), s => Assert.Null(s.Boundary));
    }

    [Fact]
    public void EnforcesByteLimitIncludingUtf8BomAndMultibyteCharacters()
    {
        var importer = new OperatorScriptImporter(new TtsScriptImportLimits(3, 10, 10));
        AssertSuccess(importer.Import(Encoding.UTF8.GetBytes("ế"), Products));
        AssertFailure(importer.Import(Encoding.UTF8.GetBytes("ếa"), Products));
        AssertFailure(importer.Import(new byte[] { 0xef, 0xbb, 0xbf, 0x61 }, Products));
    }

    [Fact]
    public void EnforcesSegmentLimitWithoutReturningPartialDocument()
    {
        var importer = new OperatorScriptImporter(new TtsScriptImportLimits(1000, 2, 100));
        AssertSuccess(importer.Import(Encoding.UTF8.GetBytes("A. B."), Products));
        AssertFailure(importer.Import(Encoding.UTF8.GetBytes("A. B. C."), Products));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(-1, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, -1)]
    public void RejectsInvalidExplicitLimits(int bytes, int segments, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperatorScriptImporter(new TtsScriptImportLimits(bytes, segments, length)));

    [Fact]
    public void RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new OperatorScriptImporter(null!));
        Assert.Throws<ArgumentNullException>(() => Create().Import(Encoding.UTF8.GetBytes("Hi"), null!));
    }

    private static OperatorScriptImporter Create(int maxLength = 100) =>
        new(new TtsScriptImportLimits(10000, 100, maxLength));
    private static TtsScriptImportResult Import(string text, int maxLength = 100) =>
        Create(maxLength).Import(Encoding.UTF8.GetBytes(text), Products);
    private static TtsScriptDocument AssertSuccess(TtsScriptImportResult result)
    {
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Errors);
        return Assert.IsType<TtsScriptDocument>(result.Document);
    }
    private static void AssertFailure(TtsScriptImportResult result)
    {
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Document);
        Assert.NotEmpty(result.Errors);
    }
}
