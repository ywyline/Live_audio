using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T061InteractionRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.Parse("4dc7d6c5-cce5-4a4e-9f20-8d3d90e60b61");

    [Fact]
    public void DefaultsPreserveVietnameseTemplatesAndBusinessLimits()
    {
        var h = new Harness();
        var options = h.Rules.Options;
        Assert.Equal(TimeSpan.FromSeconds(15), options.WelcomeCooldown);
        Assert.Equal(TimeSpan.FromSeconds(10), options.FollowCooldown);
        Assert.Equal(TimeSpan.FromSeconds(10), options.KeywordCooldown);
        Assert.Equal(TimeSpan.FromSeconds(20), options.WelcomeTtl);
        Assert.Equal(TimeSpan.FromSeconds(60), options.FollowTtl);
        Assert.Equal(TimeSpan.FromSeconds(30), options.KeywordTtl);
        Assert.Equal(20, options.FollowCapacity);
        var welcome = h.Add(LiveEventType.Enter, "1", name: "Nguy\u1ec5n \u00c1nh");
        Assert.Equal("Ch\u00e0o m\u1eebng Nguy\u1ec5n \u00c1nh \u0111\u1ebfn v\u1edbi bu\u1ed5i livestream!", welcome.RenderedText);
        var follow = h.Add(LiveEventType.Follow, "2", name: "L\u00ea Mai");
        Assert.Equal("C\u1ea3m \u01a1n L\u00ea Mai \u0111\u00e3 theo d\u00f5i!", follow.RenderedText);
        Assert.Contains("ch\u0103m s\u00f3c", InteractionTemplates.DefaultKeyword);
    }

    [Fact]
    public void WelcomeRetainsLatestOccurredAtAndFallsBackToReceivedAt()
    {
        var h = new Harness();
        h.Enqueue(h.Event(LiveEventType.Enter, "new") with { OccurredAt = Start.AddSeconds(-1) });
        h.Enqueue(h.Event(LiveEventType.Enter, "old") with { OccurredAt = Start.AddSeconds(-10) });
        Assert.Equal("user-new", h.Rules.TryPrepareNext()!.Candidate.UserId);
        h.Enqueue(h.Event(LiveEventType.Enter, "fallback") with { OccurredAt = null });
        Assert.Equal(1, h.Rules.Snapshot.WelcomeCount);
        Assert.Equal("user-fallback", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void NewWelcomeRevokesPreparedCandidateBeforePlayback()
    {
        var h = new Harness();
        h.Add(LiveEventType.Enter, "old");
        var old = h.Rules.TryPrepareNext()!.Candidate;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Add(LiveEventType.Enter, "new");
        Assert.False(h.Rules.BeginPlayback(old));
        Assert.Equal("user-new", h.Rules.TryPrepareNext()!.Candidate.UserId);
        Assert.NotEmpty(h.Rules.DrainDiscards());
    }

    [Fact]
    public void StartedWelcomeCannotBeReplacedOrInterrupted()
    {
        var h = new Harness();
        h.Add(LiveEventType.Enter, "old");
        var old = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(old));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Add(LiveEventType.Enter, "new");
        h.Add(LiveEventType.Follow, "follow");
        Assert.Null(h.Rules.TryPrepareNext());
        h.Rules.MarkVoiceCompleted(old);
        Assert.Equal(InteractionKind.Follow, h.Rules.TryPrepareNext()!.Kind);
    }

    [Fact]
    public void FollowQueueRetainsTwentyNewestCandidatesInFifoOrderAndReportsDiscard()
    {
        var h = new Harness(options: ZeroCooldowns());
        var oldest = h.Add(LiveEventType.Follow, "0");
        for (var index = 1; index <= 20; index++) h.Add(LiveEventType.Follow, index.ToString());
        Assert.Equal(20, h.Rules.Snapshot.FollowCount);
        var discarded = h.Rules.DrainDiscards();
        Assert.Single(discarded);
        Assert.Contains(oldest.CandidateId, discarded[0].ToString());
        Assert.Empty(h.Rules.DrainDiscards());
        for (var index = 1; index <= 20; index++)
        {
            var selected = h.Rules.TryPrepareNext()!.Candidate;
            Assert.Equal("user-" + index, selected.UserId);
            Assert.True(h.Rules.BeginPlayback(selected));
            h.Rules.MarkVoiceCompleted(selected);
        }
        Assert.Null(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void StartedFollowIsProtectedFromQueueOverflow()
    {
        var h = new Harness(options: ZeroCooldowns());
        h.Add(LiveEventType.Follow, "prepared");
        var prepared = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(prepared));
        for (var index = 0; index < 25; index++) h.Add(LiveEventType.Follow, index.ToString());
        Assert.Null(h.Rules.TryPrepareNext());
        h.Rules.MarkVoiceCompleted(prepared);
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Theory]
    [InlineData(LiveEventType.Enter, 20)]
    [InlineData(LiveEventType.Follow, 60)]
    [InlineData(LiveEventType.Comment, 30)]
    public void TtlExpiresAtExactBoundaryBeforeSynthesis(LiveEventType type, int ttlSeconds)
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(type, "1", "hello");
        h.Clock.Advance(TimeSpan.FromSeconds(ttlSeconds));
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.NotEmpty(h.Rules.DrainDiscards());
    }

    [Theory]
    [InlineData(LiveEventType.Enter, 20)]
    [InlineData(LiveEventType.Follow, 60)]
    [InlineData(LiveEventType.Comment, 30)]
    public void TtlIsRecheckedBeforePlaybackAtExactBoundary(LiveEventType type, int ttlSeconds)
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(type, "1", "hello");
        var prepared = h.Rules.TryPrepareNext()!.Candidate;
        h.Clock.Advance(TimeSpan.FromSeconds(ttlSeconds));
        Assert.False(h.Rules.IsFresh(prepared));
        Assert.False(h.Rules.BeginPlayback(prepared));
        Assert.NotEmpty(h.Rules.DrainDiscards());
    }

    [Fact]
    public void TtlUsesReceivedAtInsteadOfOccurredAtOrEnqueueTime()
    {
        var h = new Harness();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Enqueue(h.Event(LiveEventType.Enter, "1") with { ReceivedAt = Start, OccurredAt = Start.AddDays(-1) });
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        var prepared = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.IsFresh(prepared));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(h.Rules.BeginPlayback(prepared));
    }

    [Theory]
    [InlineData(LiveEventType.Enter, 15)]
    [InlineData(LiveEventType.Follow, 10)]
    [InlineData(LiveEventType.Comment, 10)]
    public void CategoryCooldownStartsAtCompletePlaybackAndHasExactBoundary(LiveEventType type, int seconds)
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(type, "1", "hello");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(type, "2", "hello");
        h.Clock.Advance(TimeSpan.FromSeconds(seconds).Subtract(TimeSpan.FromTicks(1)));
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("user-2", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public void WallClockJumpDoesNotChangeMonotonicCooldown(int jumpSeconds)
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Clock.JumpWallClock(TimeSpan.FromSeconds(jumpSeconds));
        h.Add(LiveEventType.Follow, "2");
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("user-2", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void IncompletePlaybackCannotStartCooldownAndReleaseAllowsNextCandidate()
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        h.Rules.MarkVoiceCompleted(first);
        h.Rules.Release(first);
        h.Add(LiveEventType.Follow, "2");
        var next = h.Rules.TryPrepareNext();
        Assert.NotNull(next);
        Assert.True(h.Rules.BeginPlayback(next!.Candidate));
        h.Rules.Release(next.Candidate);
        h.Add(LiveEventType.Follow, "3");
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void ZeroCooldownAllowsImmediateDifferentUserAfterSuccess()
    {
        var h = new Harness(options: ZeroCooldowns());
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(LiveEventType.Follow, "2");
        Assert.Equal("user-2", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void PriorityIsFollowThenKeywordThenWelcomeAndOnlyOneVoiceLeaseExists()
    {
        var h = new Harness([Rule("k", "hello")], ZeroCooldowns());
        h.Add(LiveEventType.Enter, "welcome");
        h.Add(LiveEventType.Comment, "keyword", "hello");
        h.Add(LiveEventType.Follow, "follow");
        foreach (var kind in new[] { InteractionKind.Follow, InteractionKind.Keyword, InteractionKind.Welcome })
        {
            var current = h.Rules.TryPrepareNext()!;
            Assert.Equal(kind, current.Kind);
            Assert.Null(h.Rules.TryPrepareNext());
            Assert.True(h.Rules.BeginPlayback(current.Candidate));
            Assert.Null(h.Rules.TryPrepareNext());
            h.Rules.MarkVoiceCompleted(current.Candidate);
        }
    }

    [Fact]
    public void ContextChangeRejectsOldEventsPlaybackAndCompletionCallbacks()
    {
        var h = new Harness();
        var previousEvent = h.Event(LiveEventType.Follow, "old");
        h.Enqueue(previousEvent);
        var old = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(old));
        var nextSession = Guid.Parse("148f49f6-dda9-4275-92f2-2d12067dbfda");
        h.Rules.SetContext(nextSession, "new-room");
        Assert.False(h.Enqueue(previousEvent).Accepted);
        Assert.False(h.Rules.BeginPlayback(old));
        h.Rules.MarkVoiceCompleted(old);
        h.Rules.Release(old);
        Assert.True(h.Enqueue(h.Event(LiveEventType.Follow, "new") with { SessionId = nextSession, RoomId = "new-room" }).Accepted);
        Assert.Equal("user-new", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void SameSessionRoomChangeRejectsPriorRoomAndClearsCandidates()
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "old");
        h.Rules.SetContext(SessionId, "room-2");
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.False(h.Enqueue(h.Event(LiveEventType.Follow, "wrong-room")).Accepted);
        Assert.True(h.Enqueue(h.Event(LiveEventType.Follow, "new") with { RoomId = "room-2" }).Accepted);
    }

    [Theory]
    [InlineData(KeywordMatchMode.Exact, KeywordMatchRequirement.Any, "sale", true)]
    [InlineData(KeywordMatchMode.Exact, KeywordMatchRequirement.Any, "wholesale", false)]
    [InlineData(KeywordMatchMode.Contains, KeywordMatchRequirement.Any, "wholesale", true)]
    [InlineData(KeywordMatchMode.Contains, KeywordMatchRequirement.All, "sale vip", true)]
    [InlineData(KeywordMatchMode.Contains, KeywordMatchRequirement.All, "sale", false)]
    [InlineData(KeywordMatchMode.Exact, KeywordMatchRequirement.All, "sale vip", false)]
    public void KeywordExactContainsAnyAndAllProduceExpectedMatches(KeywordMatchMode mode, KeywordMatchRequirement requirement, string text, bool match)
    {
        var h = new Harness([new KeywordRule("k", new[] { "sale", "vip" }, mode, requirement)]);
        var result = h.Enqueue(h.Event(LiveEventType.Comment, "1", text));
        Assert.Equal(match, result.Accepted);
        if (!match) Assert.Equal(InteractionAdmissionStatus.NoMatch, result.Status);
    }

    [Fact]
    public void KeywordMatchingNormalizesUnicodeCaseButRetainsVietnameseAccents()
    {
        var h = new Harness([Rule("k", "ch\u00e0o")]);
        Assert.True(h.Enqueue(h.Event(LiveEventType.Comment, "1", "CHA\u0300O")).Accepted);
        Assert.False(h.Enqueue(h.Event(LiveEventType.Comment, "2", "chao")).Accepted);
    }

    [Fact]
    public void KeywordRetainsOnlyLatestMatchingCommentAndIgnoresUnmatchedComments()
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(LiveEventType.Comment, "old", "hello");
        h.Add(LiveEventType.Comment, "new", "hello again");
        Assert.Equal(InteractionAdmissionStatus.NoMatch, h.Enqueue(h.Event(LiveEventType.Comment, "miss", "goodbye")).Status);
        Assert.Equal(1, h.Rules.Snapshot.KeywordCount);
        Assert.Equal("user-new", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void PriorityThenRuleIdSelectsOneKeywordRuleRegardlessOfInputOrder()
    {
        var rules = new[] { Rule("z", "hello") with { Priority = 5 }, Rule("a", "hello") with { Priority = 5 }, Rule("high", "hello") with { Priority = 9 } };
        Assert.Equal("high", new Harness(rules).Add(LiveEventType.Comment, "1", "hello").RuleId);
        Assert.Equal("a", new Harness(rules.Take(2)).Add(LiveEventType.Comment, "2", "hello").RuleId);
        Assert.Equal("a", new Harness(rules.Take(2).Reverse()).Add(LiveEventType.Comment, "3", "hello").RuleId);
    }

    [Fact]
    public void FullyDisabledHighestRuleDoesNotHideEnabledRule()
    {
        var disabled = Rule("disabled", "hello") with { Priority = 99, VoiceEnabled = false, TextEnabled = false };
        var h = new Harness([disabled, Rule("enabled", "hello")]);
        Assert.Equal("enabled", h.Add(LiveEventType.Comment, "1", "hello").RuleId);
    }

    [Fact]
    public void TextSelectionDoesNotConsumeVoiceWaitingForCooldown()
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(LiveEventType.Comment, "1", "hello");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(LiveEventType.Comment, "2", "hello");
        Assert.Null(h.Rules.TryPrepareNext());
        var text = h.Rules.TrySelectTextNext();
        Assert.NotNull(text);
        Assert.Equal("user-2", text!.Candidate.UserId);
        Assert.Null(h.Rules.TrySelectTextNext());
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("user-2", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VoiceAndTextCanBeEnabledIndependently(bool voice, bool text)
    {
        var h = new Harness([Rule("k", "hello") with { VoiceEnabled = voice, TextEnabled = text }]);
        h.Add(LiveEventType.Comment, "1", "hello");
        Assert.Equal(voice, h.Rules.TryPrepareNext() is not null);
        Assert.Equal(text, h.Rules.TrySelectTextNext() is not null);
        Assert.Null(h.Rules.TrySelectTextNext());
    }

    [Fact]
    public void RuleCooldownAndGlobalKeywordCooldownMustBothExpire()
    {
        var h = new Harness([Rule("a", "alpha") with { Cooldown = TimeSpan.FromSeconds(20) }, Rule("b", "beta")]);
        h.Add(LiveEventType.Comment, "1", "alpha");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(LiveEventType.Comment, "2", "beta");
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var second = h.Rules.TryPrepareNext()!.Candidate;
        Assert.Equal("b", second.RuleId);
        h.Rules.Release(second);
        h.Add(LiveEventType.Comment, "3", "alpha");
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("a", h.Rules.TryPrepareNext()!.Candidate.RuleId);
    }

    [Fact]
    public void MissingStableUserOrEmptyCommentCannotCreateCandidate()
    {
        var h = new Harness([Rule("k", "hello")]);
        Assert.False(h.Enqueue(h.Event(LiveEventType.Follow, "1") with { UserId = null }).Accepted);
        Assert.False(h.Enqueue(h.Event(LiveEventType.Comment, "2", " \u0001 ")).Accepted);
        Assert.Equal(0, h.Rules.Snapshot.FollowCount);
        Assert.Equal(0, h.Rules.Snapshot.KeywordCount);
        Assert.Null(h.Rules.TryPrepareNext());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void InvalidCategoryCooldownsAreRejected(int seconds)
    {
        var invalid = TimeSpan.FromSeconds(seconds);
        foreach (var options in new[] { new InteractionPolicyOptions { WelcomeCooldown = invalid }, new InteractionPolicyOptions { FollowCooldown = invalid }, new InteractionPolicyOptions { KeywordCooldown = invalid } })
            Assert.ThrowsAny<ArgumentException>(() => new Harness(options: options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public void CooldownLimitsAreInclusive(int seconds)
    {
        var value = TimeSpan.FromSeconds(seconds);
        var h = new Harness(options: new InteractionPolicyOptions { WelcomeCooldown = value, FollowCooldown = value, KeywordCooldown = value });
        Assert.Equal(value, h.Rules.Options.KeywordCooldown);
    }

    [Fact]
    public void InvalidKeywordRulesFailAtCoordinatorConstruction()
    {
        var valid = Rule("k", "hello");
        foreach (var invalid in new[] { valid with { RuleId = " " }, valid with { Keywords = Array.Empty<string>() }, valid with { Keywords = new[] { " " } }, valid with { Cooldown = TimeSpan.FromSeconds(-1) }, valid with { Cooldown = TimeSpan.FromSeconds(3601) }, valid with { MatchMode = (KeywordMatchMode)99 }, valid with { Requirement = (KeywordMatchRequirement)99 } })
            Assert.ThrowsAny<ArgumentException>(() => new Harness([invalid]));
        Assert.ThrowsAny<ArgumentException>(() => new Harness([valid, valid]));
    }

    [Fact]
    public void CallerMutationOfRulesAndKeywordCollectionsCannotAlterFrozenConfiguration()
    {
        var words = new List<string> { "hello" };
        var rules = new List<KeywordRule> { new("k", (IReadOnlyList<string>)words) };
        var h = new Harness(rules);
        words.Clear();
        words.Add("changed");
        rules.Clear();
        Assert.True(h.Enqueue(h.Event(LiveEventType.Comment, "1", "hello")).Accepted);
        Assert.False(h.Enqueue(h.Event(LiveEventType.Comment, "2", "changed")).Accepted);
    }

    [Fact]
    public void SanitizationKeepsAccentsRemovesDecorationsControlsAndProductMarkers()
    {
        Assert.Equal("Nguy\u1ec5n", InteractionTextSanitizer.Sanitize("  Nguy\u1ec5n\u0007 @[12] \ud83d\ude00 ", 64));
        Assert.Equal("\u0110\u1ed7 Mai", InteractionTextSanitizer.Sanitize("\u0110\u1ed7 @[5] Mai", 64));
        Assert.Equal("ch\u00e0o", InteractionTextSanitizer.Sanitize("cha\u0300o", 64));
        Assert.Equal("abcd", InteractionTextSanitizer.Sanitize("abcdef", 4));
    }

    [Fact]
    public void MalformedUtf16IsHandledWithoutThrowingOrLeakingBrokenSurrogates()
    {
        var value = InteractionTextSanitizer.Sanitize("An\ud800A\udfff\u0001B", 64);
        Assert.DoesNotContain('\ud800', value);
        Assert.DoesNotContain('\udfff', value);
        Assert.DoesNotContain('\u0001', value);
        Assert.Contains("An", value);
        Assert.Contains("B", value);
    }

    [Fact]
    public void ProductMarkerIsRemovedBeforeLengthTruncation()
    {
        var value = InteractionTextSanitizer.Sanitize("An@[12345]B", 4);
        Assert.Equal("AnB", value);
        Assert.DoesNotContain("@[", value);
    }

    [Fact]
    public void InsertedUserTextCannotRecursivelyExpandTemplatePlaceholders()
    {
        var rendered = InteractionTemplates.Render("{userName}: {comment}", "{comment}", "secret @[8]");
        Assert.Equal("{comment}: secret", rendered);

        Assert.DoesNotContain("@[", rendered);
    }

    [Fact]
    public void RuleOrderBreaksPriorityTieBeforeStableRuleId()
    {
        var h = new Harness([Rule("a", "hello") with { Priority = 5, Order = 20 }, Rule("z", "hello") with { Priority = 5, Order = 10 }]);
        Assert.Equal("z", h.Add(LiveEventType.Comment, "1", "hello").RuleId);
    }

    [Fact]
    public void ExplicitlyDisabledRuleIsNotMatched()
    {
        var h = new Harness([Rule("disabled", "hello") with { Priority = 99, Enabled = false }, Rule("enabled", "hello")]);
        Assert.Equal("enabled", h.Add(LiveEventType.Comment, "1", "hello").RuleId);
    }

    [Fact]
    public void WelcomeAndFollowDoNotProduceTextReplies()
    {
        var h = new Harness();
        Assert.False(h.Add(LiveEventType.Enter, "1").TextEnabled);
        Assert.False(h.Add(LiveEventType.Follow, "2").TextEnabled);
        Assert.Null(h.Rules.TrySelectTextNext());
    }

    [Fact]
    public void VoiceAndTextChooseDifferentTemplatesUsingInjectedRandom()
    {
        var random = new SequenceRandom(1, 0);
        var h = new Harness([Rule("k", "hello") with { VoiceTemplates = new[] { "Voice A {userName}", "Voice B {userName}" }, TextTemplates = new[] { "Text A {comment}", "Text B {comment}" } }], random: random);
        h.Add(LiveEventType.Comment, "1", "hello", "Mai");
        Assert.Equal("Voice B Mai", h.Rules.TryPrepareNext()!.Text);
        Assert.Equal("Text A hello", h.Rules.TrySelectTextNext()!.Text);
        Assert.Equal(2, random.Calls);
    }

    [Fact]
    public void RandomWelcomeTemplatesAndCallerMutationRemainControlled()
    {
        var choices = new List<string> { "Hello A {userName}", "Hello B {userName}" };
        var h = new Harness(options: new InteractionPolicyOptions { WelcomeTemplates = choices }, random: new SequenceRandom(1));
        choices.Clear();
        choices.Add("Changed");
        Assert.Equal("Hello B Mai", h.Add(LiveEventType.Enter, "1", name: "Mai").RenderedText);
    }

    [Fact]
    public void CallerMutationCannotChangePerChannelTemplates()
    {
        var voices = new List<string> { "Voice original" };
        var texts = new List<string> { "Text original" };
        var h = new Harness([Rule("k", "hello") with { VoiceTemplates = voices, TextTemplates = texts }]);
        voices[0] = "Changed voice";
        texts[0] = "Changed text";
        h.Add(LiveEventType.Comment, "1", "hello");
        Assert.Equal("Voice original", h.Rules.TryPrepareNext()!.Text);
        Assert.Equal("Text original", h.Rules.TrySelectTextNext()!.Text);
    }

    [Fact]
    public void InvalidLimitsAndTemplatesAreRejected()
    {
        foreach (var options in new[]
        {
            new InteractionPolicyOptions { WelcomeTtl = TimeSpan.Zero },
            new InteractionPolicyOptions { FollowTtl = TimeSpan.FromSeconds(-1) },
            new InteractionPolicyOptions { KeywordTtl = TimeSpan.Zero },
            new InteractionPolicyOptions { FollowCapacity = 0 },
            new InteractionPolicyOptions { MaxDisplayNameLength = 0 },
            new InteractionPolicyOptions { MaxCommentLength = 0 },
            new InteractionPolicyOptions { WelcomeTemplate = "{unknown}" },
            new InteractionPolicyOptions { FollowTemplate = "{" },
            new InteractionPolicyOptions { WelcomeTemplates = Array.Empty<string>() }
        }) Assert.ThrowsAny<ArgumentException>(() => new Harness(options: options));
        Assert.ThrowsAny<ArgumentException>(() => new Harness([Rule("k", "hello") with { VoiceTemplates = new[] { "{invalid}" } }]));
        Assert.ThrowsAny<ArgumentException>(() => new Harness([Rule("k", "hello") with { TextTemplates = Array.Empty<string>() }]));
    }

    [Fact]
    public void TruncationCannotTurnLongCommentIntoAnExactKeywordMatch()
    {
        var h = new Harness([Rule("k", "hello") with { MatchMode = KeywordMatchMode.Exact }], new InteractionPolicyOptions { MaxCommentLength = 5 });
        Assert.Equal(InteractionAdmissionStatus.NoMatch, h.Enqueue(h.Event(LiveEventType.Comment, "1", "hello again")).Status);
    }

    [Fact]
    public void SameContextReconnectPreservesLeaseAndCooldown()
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        h.Rules.SetContext(SessionId, "test-room");
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(LiveEventType.Follow, "2");
        h.Rules.SetContext(SessionId, "test-room");
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void DuplicateCompletionDoesNotExtendCooldown()
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        h.Rules.MarkVoiceCompleted(first);
        h.Add(LiveEventType.Follow, "2");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void EnqueueRequiresSuccessfulT060AdmissionAndTargetedReservation()
    {
        var h = new Harness();
        var liveEvent = h.Event(LiveEventType.Follow, "1");
        Assert.False(h.Rules.Enqueue(liveEvent).Accepted);
        foreach (var status in new[] { EventProcessStatus.Duplicate, EventProcessStatus.Completed, EventProcessStatus.Cancelled, EventProcessStatus.Failed, EventProcessStatus.Unknown, EventProcessStatus.SkippedMissingUserId })
            Assert.False(h.Rules.Enqueue(liveEvent, new(status, "fp", EventIdentityQuality.Complete)).Accepted);
        Assert.False(h.Rules.Enqueue(liveEvent, new(EventProcessStatus.Accepted, "fp", EventIdentityQuality.Complete)).Accepted);
        Assert.False(h.Rules.Enqueue(liveEvent, new(EventProcessStatus.Reserved, "fp", EventIdentityQuality.Complete)).Accepted);
        Assert.Null(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void DiscardsPreserveUpstreamIdentifiersForReservationRelease()
    {
        var h = new Harness();
        var liveEvent = h.Event(LiveEventType.Enter, "1");
        var reservation = Guid.Parse("02b13e6c-f5a8-4512-8ad3-e0b0fbd94ca2");
        var candidate = h.Rules.Enqueue(liveEvent, new(EventProcessStatus.Reserved, "fp-1", EventIdentityQuality.Complete, reservation)).Candidate!;
        h.Add(LiveEventType.Enter, "2");
        var discarded = Assert.Single(h.Rules.DrainDiscards()).Candidate;
        Assert.Equal(candidate.CandidateId, discarded.CandidateId);
        Assert.Equal("test-source", discarded.SourceId);
        Assert.Equal("1", discarded.EventId);
        Assert.Equal(reservation, discarded.ReservationId);
        Assert.Equal("fp-1", discarded.Fingerprint);
    }

    [Fact]
    public void MissingUserIdCommentStillMatchesWithoutTargetedUserReservation()
    {
        var h = new Harness([Rule("k", "hello")]);
        var liveEvent = h.Event(LiveEventType.Comment, "1", "hello") with { UserId = null, IdentityQuality = EventIdentityQuality.MissingUserId };
        Assert.True(h.Enqueue(liveEvent).Accepted);
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void StopRejectsOldEventsAndCallbacksUntilContextIsExplicitlyRestarted()
    {
        var h = new Harness();
        h.Add(LiveEventType.Follow, "1");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.Stop();
        Assert.False(h.Enqueue(h.Event(LiveEventType.Follow, "2")).Accepted);
        h.Rules.MarkVoiceCompleted(first);
        Assert.False(h.Rules.BeginPlayback(first));
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.NotEmpty(h.Rules.DrainDiscards());
        h.Rules.SetContext(SessionId, "test-room");
        h.Add(LiveEventType.Follow, "3");
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void GlobalChannelDisablePreventsUnusableRuleFromWinning()
    {
        var voiceOnly = Rule("voice", "hello") with { Priority = 99, VoiceEnabled = true, TextEnabled = false };
        var textOnly = Rule("text", "hello") with { VoiceEnabled = false, TextEnabled = true };
        var h = new Harness([voiceOnly, textOnly], new InteractionPolicyOptions { VoiceEnabled = false });
        Assert.Equal("text", h.Add(LiveEventType.Comment, "1", "hello").RuleId);
        Assert.NotNull(h.Rules.TrySelectTextNext());
        Assert.Null(h.Rules.TryPrepareNext());
    }

    [Theory]
    [InlineData("xin\n\tch\u00e0o")]
    [InlineData("xin @\n[\t1 \n2\t] ch\u00e0o")]
    [InlineData("xin @[\u00001] ch\u00e0o")]
    public void RemoteWhitespaceAndObfuscatedMarkersAreNormalizedSafely(string input)
    {
        Assert.Equal("xin ch\u00e0o", InteractionTextSanitizer.Sanitize(input, 64));
    }

    [Fact]
    public void MoreThanOneThousandRulesAreRejected()
    {
        var rules = Enumerable.Range(0, 1001).Select(index => Rule(index.ToString(), "hello"));
        Assert.ThrowsAny<ArgumentException>(() => new Harness(rules));
    }

    [Fact]
    public void DiscardBacklogIsBoundedAndResumesAfterDrain()
    {
        var h = new Harness();
        var accepted = new List<InteractionCandidate>();
        for (var index = 0; index < 300; index++)
        {
            var result = h.Enqueue(h.Event(LiveEventType.Enter, index.ToString()));
            if (result.Accepted) accepted.Add(result.Candidate!);
        }
        Assert.Equal(InteractionRuleCoordinator.DiscardCapacity, accepted.Count);
        var discarded = h.Rules.DrainDiscards();
        Assert.Equal(InteractionRuleCoordinator.DiscardCapacity - 1, discarded.Count);
        Assert.Equal(accepted.Take(accepted.Count - 1).Select(item => item.ReservationId),
            discarded.Select(item => item.Candidate.ReservationId));
        Assert.Equal(accepted.Last().CandidateId, h.Rules.TryPrepareNext()!.Candidate.CandidateId);
        Assert.True(h.Enqueue(h.Event(LiveEventType.Enter, "after-drain")).Accepted);
    }
    [Fact]
    public void ConcurrentFollowAdmissionsPreserveAllReservationsWithinCapacity()
    {
        var h = new Harness(options: ZeroCooldowns());
        var admissions = new System.Collections.Concurrent.ConcurrentBag<InteractionCandidate>();
        Parallel.For(0, 100, index => admissions.Add(h.Add(LiveEventType.Follow, index.ToString())));
        Assert.Equal(20, h.Rules.Snapshot.FollowCount);
        var discarded = h.Rules.DrainDiscards();
        Assert.Equal(80, discarded.Count);
        var remaining = new List<InteractionCandidate>();
        while (h.Rules.TryPrepareNext() is { } next)
        {
            remaining.Add(next.Candidate);
            Assert.True(h.Rules.BeginPlayback(next.Candidate));
            h.Rules.MarkVoiceCompleted(next.Candidate);
        }
        Assert.Equal(20, remaining.Count);
        var observed = discarded.Select(item => item.Candidate).Concat(remaining).ToArray();
        Assert.Equal(100, observed.Select(item => item.ReservationId).Distinct().Count());
        Assert.Equal(admissions.Select(item => item.CandidateId).Order(), observed.Select(item => item.CandidateId).Order());
    }
    [Theory]
    [InlineData("ab", "a+b")]
    [InlineData("hello", "hello@[1]")]
    [InlineData("hello", "<b>hello</b>")]
    [InlineData("hello", "hello\ud83d\ude00")]
    public void ExactMatchDoesNotGainAHitByRemovingRemoteMarkupOrSymbols(string keyword, string comment)
    {
        var h = new Harness([Rule("exact", keyword) with { MatchMode = KeywordMatchMode.Exact }]);
        Assert.Equal(InteractionAdmissionStatus.NoMatch, h.Enqueue(h.Event(LiveEventType.Comment, "1", comment)).Status);
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.Null(h.Rules.TrySelectTextNext());
    }

    [Fact]
    public void ExactMatchingStillAcceptsCanonicallyEquivalentVietnamese()
    {
        var h = new Harness([Rule("exact", "ch\u00e0o") with { MatchMode = KeywordMatchMode.Exact }]);
        Assert.True(h.Enqueue(h.Event(LiveEventType.Comment, "1", "CHA\u0300O")).Accepted);
        Assert.NotNull(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void VoiceFailureLeavesTextSelectableAndDoesNotStartVoiceCooldown()
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(LiveEventType.Comment, "1", "hello");
        var first = h.Rules.TryPrepareNext()!.Candidate;
        h.Rules.Release(first, InteractionChannel.Voice);
        Assert.False(h.Rules.BeginPlayback(first));
        var text = h.Rules.TrySelectTextNext();
        Assert.NotNull(text);
        Assert.Same(first, text!.Candidate);
        h.Rules.MarkTextCompleted(text.Candidate);
        h.Add(LiveEventType.Comment, "2", "hello");
        Assert.Equal("user-2", h.Rules.TryPrepareNext()!.Candidate.UserId);
    }

    [Fact]
    public void TextFailureLeavesVoicePlayableAndDoesNotStartTextCooldown()
    {
        var h = new Harness([Rule("k", "hello") with { TextCooldown = TimeSpan.FromSeconds(30) }],
            new InteractionPolicyOptions { TextCooldown = TimeSpan.FromSeconds(30) });
        h.Add(LiveEventType.Comment, "1", "hello");
        var text = h.Rules.TrySelectTextNext()!.Candidate;
        h.Rules.Release(text, InteractionChannel.Text);
        var voice = h.Rules.TryPrepareNext();
        Assert.NotNull(voice);
        Assert.Same(text, voice!.Candidate);
        Assert.True(h.Rules.BeginPlayback(voice.Candidate));
        h.Rules.MarkVoiceCompleted(voice.Candidate);
        h.Add(LiveEventType.Comment, "2", "hello");
        Assert.Equal("user-2", h.Rules.TrySelectTextNext()!.Candidate.UserId);
        Assert.Null(h.Rules.TryPrepareNext());
    }

    [Fact]
    public void ReleaseWithoutChannelCancelsBothOutstandingLeases()
    {
        var h = new Harness([Rule("k", "hello")]);
        h.Add(LiveEventType.Comment, "1", "hello");
        var voice = h.Rules.TryPrepareNext()!.Candidate;
        var text = h.Rules.TrySelectTextNext()!.Candidate;
        Assert.Same(voice, text);
        h.Rules.Release(voice);
        Assert.False(h.Rules.BeginPlayback(voice));
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.Null(h.Rules.TrySelectTextNext());
        Assert.Same(voice, Assert.Single(h.Rules.DrainDiscards()).Candidate);
        h.Rules.MarkVoiceCompleted(voice);
        h.Rules.MarkTextCompleted(text);
        h.Add(LiveEventType.Comment, "2", "hello");
        Assert.NotNull(h.Rules.TryPrepareNext());
        Assert.NotNull(h.Rules.TrySelectTextNext());
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public void TtlRemainingTimeIsFixedOnAdmissionDespiteWallClockJump(int jumpSeconds)
    {
        var h = new Harness();
        var receivedFiveSecondsAgo = h.Event(LiveEventType.Enter, "1") with { ReceivedAt = Start.AddSeconds(-5) };
        var admission = h.Enqueue(receivedFiveSecondsAgo);
        Assert.True(admission.Accepted);
        h.Clock.JumpWallClock(TimeSpan.FromSeconds(jumpSeconds));
        h.Clock.Advance(TimeSpan.FromSeconds(15).Subtract(TimeSpan.FromTicks(1)));
        Assert.True(h.Rules.IsFresh(admission.Candidate!));
        var prepared = h.Rules.TryPrepareNext()!.Candidate;
        h.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(h.Rules.IsFresh(prepared));
        Assert.False(h.Rules.BeginPlayback(prepared));
        Assert.Same(prepared, Assert.Single(h.Rules.DrainDiscards()).Candidate);
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public void WaitingCandidateExpiresAtMonotonicDeadlineBeforePreparation(int jumpSeconds)
    {
        var h = new Harness();
        h.Add(LiveEventType.Enter, "1");
        h.Clock.JumpWallClock(TimeSpan.FromSeconds(jumpSeconds));
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.Single(h.Rules.DrainDiscards());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public void FutureReceivedAtHasZeroInitialAgeAndCannotExtendTtl(int futureSeconds)
    {
        var h = new Harness();
        var future = h.Event(LiveEventType.Enter, "1") with { ReceivedAt = Start.AddSeconds(futureSeconds) };
        Assert.True(h.Enqueue(future).Accepted);
        h.Clock.Advance(TimeSpan.FromSeconds(20).Subtract(TimeSpan.FromTicks(1)));
        var prepared = h.Rules.TryPrepareNext()!.Candidate;
        Assert.True(h.Rules.IsFresh(prepared));
        h.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(h.Rules.IsFresh(prepared));
        Assert.False(h.Rules.BeginPlayback(prepared));
        Assert.Single(h.Rules.DrainDiscards());
    }
    private sealed class SequenceRandom(params int[] values) : IRandomSource
    {
        public int Calls { get; private set; }
        public int NextInt32(int minInclusive, int maxExclusive)
        {
            var value = values[Calls++];
            Assert.InRange(value, minInclusive, maxExclusive - 1);
            return value;
        }
        public double NextDouble() => throw new InvalidOperationException("Template selection must use integer indexes.");
    }
    private static KeywordRule Rule(string id, string word) => new(id, (IReadOnlyList<string>)new[] { word });
    private static InteractionPolicyOptions ZeroCooldowns() => new() { WelcomeCooldown = TimeSpan.Zero, FollowCooldown = TimeSpan.Zero, KeywordCooldown = TimeSpan.Zero };

    private sealed class Harness
    {
        public TestClock Clock { get; } = new();
        public InteractionRuleCoordinator Rules { get; }
        public Harness(IEnumerable<KeywordRule>? rules = null, InteractionPolicyOptions? options = null, IRandomSource? random = null)
        {
            Rules = new InteractionRuleCoordinator(Clock, rules, options, random);
            Rules.SetContext(SessionId, "test-room");
        }
        public LiveEvent Event(LiveEventType type, string id, string? content = null, string name = "Tester") => new("test-source", SessionId, "test-room", type, id, "user-" + id, name, Clock.UtcNow, Clock.UtcNow, content, null, null, EventIdentityQuality.Complete);
        public InteractionAdmissionResult Enqueue(LiveEvent liveEvent)
        {
            var targeted = liveEvent.EventType is LiveEventType.Enter or LiveEventType.Follow;
            return Rules.Enqueue(liveEvent, new EventProcessResult(targeted ? EventProcessStatus.Reserved : EventProcessStatus.Accepted,
                "fingerprint-" + liveEvent.EventId, liveEvent.IdentityQuality, targeted ? Guid.NewGuid() : null));
        }
        public InteractionCandidate Add(LiveEventType type, string id, string? content = null, string name = "Tester")
        {
            var admission = Enqueue(Event(type, id, content, name));
            Assert.True(admission.Accepted, admission.Detail);
            return Assert.IsType<InteractionCandidate>(admission.Candidate);
        }
    }

    private sealed class TestClock : IClock
    {
        private long ticks;
        private TimeSpan wallOffset;
        public MonotonicTimestamp Now => new(ticks);
        public DateTimeOffset UtcNow => Start.AddTicks(ticks).Add(wallOffset);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
        public void JumpWallClock(TimeSpan offset) => wallOffset += offset;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => throw new InvalidOperationException("Rule tests must not schedule delays.");
    }
}