// 手动推进真实 30 分钟期限，无需等待墙钟或依赖调度速度。
internal sealed class ExportTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan time)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _ticks += time.Ticks;
            due = _timers.Where(t => !t.Disposed && t.Due <= _ticks).ToArray();
            foreach (var timer in due)
                timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class ManualTimer(ExportTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal readonly TimerCallback Callback = callback;
        internal readonly object? State = state;
        internal long Due, Period;
        internal bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                Period = period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (clock._gate) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
