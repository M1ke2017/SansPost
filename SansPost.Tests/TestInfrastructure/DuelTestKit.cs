using System.Text.Json;
using SansPost.Features.Duels;

namespace SansPost.Tests.TestInfrastructure
{
    // Zegar z ręcznie przesuwanym czasem i timerami: Advance odpala zaległe timery synchronicznie, w kolejności terminów —
    // testy zegara rundy, wygasania wyzwań i okna powrotu bez czekania i bez niedeterminizmu.
    public sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public int ActiveTimers
        {
            get { lock (_gate) return _timers.Count(t => t.Due is not null); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate)
                target = _now + by;

            while (true)
            {
                ManualTimer? next;
                lock (_gate)
                {
                    next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                    if (next is null)
                    {
                        _now = target;
                        return;
                    }
                    _now = next.Due!.Value;
                    next.Due = null;   // jednorazowy (period nieużywany przez serwisy)
                }
                next.Fire();
            }
        }

        private void Forget(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Fire() => _callback(_state);

            public void Dispose()
            {
                Due = null;
                _owner.Forget(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // Zdarzenia wysłane przez serwisy gry (zamiast SignalR) — do sprawdzenia adresatów i treści.
    public sealed class RecordingNotifier : IDuelNotifier
    {
        private readonly List<DuelEvent> _events = new();

        public IReadOnlyList<DuelEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public Task PublishAsync(IReadOnlyList<DuelEvent> events)
        {
            lock (_events)
                _events.AddRange(events);
            return Task.CompletedTask;
        }

        public IReadOnlyList<T> Payloads<T>(int userId, string name) =>
            Events.Where(e => e.UserId == userId && e.Name == name).Select(e => (T)e.Payload!).ToList();

        public int Count(int? userId, string name) => Events.Count(e => e.UserId == userId && e.Name == name);

        // Wszystko, co dostał dany gracz, jako JSON (jak na kablu SignalR) — do sprawdzania, czy nie wyciekła ukryta karta.
        public string WireFor(int userId) =>
            string.Join("\n", Events.Where(e => e.UserId == userId).Select(e => e.Name + " " + JsonSerializer.Serialize(e.Payload, e.Payload?.GetType() ?? typeof(object), new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        public void Clear()
        {
            lock (_events)
                _events.Clear();
        }
    }
}
