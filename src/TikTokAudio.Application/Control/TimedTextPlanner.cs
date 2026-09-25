using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Application.Control;

public enum TextTemplateOrder { Sequential, Random }

public sealed record TimedTextSchedule(
    string ScheduleId,
    IReadOnlyList<string> Messages,
    TimeSpan Interval,
    TimeSpan TimeToLive,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    TextTemplateOrder Order = TextTemplateOrder.Sequential,
    bool Enabled = true);

public sealed record TimedTextMessage(
    string ScheduleId,
    string Text,
    TimeSpan TimeToLive,
    DateTimeOffset EndsAtUtc);

/// <summary>Plans bounded future text work; it never sends or catches up missed intervals.</summary>
public sealed class TimedTextPlanner
{
    private const int MaximumMessages = 1000;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly int _capacity;
    private List<ScheduleState> _states = [];

    public TimedTextPlanner(IClock clock, IRandomSource random, int capacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1024);
        _clock = clock;
        _random = random;
        _capacity = capacity;
    }

    public void Configure(IEnumerable<TimedTextSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var replacement = new List<ScheduleState>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in schedules)
        {
            if (replacement.Count == _capacity)
                throw new ArgumentException("The schedule capacity was exceeded.", nameof(schedules));
            if (schedule is null)
                throw new ArgumentException("Schedules cannot contain null entries.", nameof(schedules));
            if (string.IsNullOrWhiteSpace(schedule.ScheduleId) || !identifiers.Add(schedule.ScheduleId))
                throw new ArgumentException("Schedule identifiers must be nonempty and unique.", nameof(schedules));
            if (schedule.Interval <= TimeSpan.Zero || schedule.TimeToLive <= TimeSpan.Zero)
                throw new ArgumentException("Schedule intervals and lifetimes must be positive.", nameof(schedules));
            if (schedule.StartsAtUtc >= schedule.EndsAtUtc)
                throw new ArgumentException("Schedule windows must have a start before their end.", nameof(schedules));
            if (!Enum.IsDefined(schedule.Order))
                throw new ArgumentException("The template order is unsupported.", nameof(schedules));
            if (schedule.Messages is null || schedule.Messages.Count is < 1 or > MaximumMessages)
                throw new ArgumentException("Each schedule requires between 1 and 1000 messages.", nameof(schedules));

            var messages = new string[schedule.Messages.Count];
            for (var index = 0; index < messages.Length; index++)
            {
                messages[index] = TimedTextContent.Normalize(schedule.Messages[index]);
            }

            replacement.Add(new ScheduleState(schedule with
            {
                Messages = messages,
                StartsAtUtc = schedule.StartsAtUtc.ToUniversalTime(),
                EndsAtUtc = schedule.EndsAtUtc.ToUniversalTime()
            }));
        }

        lock (_gate)
        {
            ResetStates(replacement, _clock.Now.Ticks, _clock.UtcNow);
            _states = replacement;
        }
    }

    public void Reset()
    {
        lock (_gate)
            ResetStates(_states, _clock.Now.Ticks, _clock.UtcNow);
    }

    public IReadOnlyList<TimedTextMessage> TakeDue()
    {
        lock (_gate)
        {
            var now = _clock.Now.Ticks;
            var utcNow = _clock.UtcNow;
            _states.RemoveAll(state => utcNow >= state.Schedule.EndsAtUtc);
            var due = new List<TimedTextMessage>();
            foreach (var state in _states)
            {
                var schedule = state.Schedule;
                if (!schedule.Enabled || utcNow < schedule.StartsAtUtc || state.DueTicks is not { } dueTicks || now < dueTicks)
                    continue;

                var index = schedule.Order == TextTemplateOrder.Random
                    ? _random.NextInt32(0, schedule.Messages.Count)
                    : state.NextMessage;
                due.Add(new TimedTextMessage(schedule.ScheduleId, schedule.Messages[index], schedule.TimeToLive, schedule.EndsAtUtc));
                if (schedule.Order == TextTemplateOrder.Sequential)
                    state.NextMessage = (index + 1) % schedule.Messages.Count;

                // A late pump produces one message, then starts a new future interval.
                state.DueTicks = FutureTicks(now, schedule.Interval.Ticks);
            }
            return due;
        }
    }

    public bool IsEnabled(string scheduleId)
    {
        lock (_gate)
        {
            var utcNow = _clock.UtcNow;
            _states.RemoveAll(state => utcNow >= state.Schedule.EndsAtUtc);
            return _states.Any(state =>
                state.Schedule.ScheduleId == scheduleId && state.Schedule.Enabled && utcNow >= state.Schedule.StartsAtUtc);
        }
    }

    private static void ResetStates(List<ScheduleState> states, long now, DateTimeOffset utcNow)
    {
        states.RemoveAll(state => utcNow >= state.Schedule.EndsAtUtc);
        foreach (var state in states)
        {
            var wait = state.Schedule.Interval;
            if (state.Schedule.StartsAtUtc > utcNow && state.Schedule.StartsAtUtc - utcNow > wait)
                wait = state.Schedule.StartsAtUtc - utcNow;
            state.DueTicks = state.Schedule.Enabled ? FutureTicks(now, wait.Ticks) : null;
        }
    }

    private static long? FutureTicks(long now, long delay) => now > long.MaxValue - delay ? null : now + delay;

    private sealed class ScheduleState(TimedTextSchedule schedule)
    {
        public TimedTextSchedule Schedule { get; } = schedule;
        public long? DueTicks { get; set; }
        public int NextMessage { get; set; }
    }
}
