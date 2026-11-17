namespace Backthread;

internal sealed record Segment(LiveWindow Window, long Start, long? End)
{
    internal long Seconds(long now) => Math.Max(0, ((End ?? now) - Start) / 1000);
}

// Timestamps here are monotonic. Neither this model nor the desktop layer writes to disk.
internal sealed class Trail
{
    internal const int MinFocusSeconds = 45;
    internal const int MaxSegments = 60;
    internal const int LookbackSeconds = 900;
    private readonly List<Segment> segments = [];
    internal IReadOnlyList<Segment> Segments => segments;
    internal LiveWindow? MarkedWindow { get; private set; }

    internal static bool Same(LiveWindow? a, LiveWindow? b) =>
        a is not null && b is not null && a.Handle == b.Handle &&
        a.ProcessId == b.ProcessId && a.Identity == b.Identity;

    internal bool Observe(LiveWindow? window, long tick)
    {
        var tail = segments.LastOrDefault();
        if (window is not null && tail is { End: null } && Same(tail.Window, window))
        {
            if (tail.Window == window) return false;
            segments[^1] = tail with { Window = window };
            return true;
        }
        bool ended = tail is { End: null };
        if (ended) segments[^1] = tail! with { End = Math.Max(tail!.Start, tick) };
        if (window is null) return ended;
        segments.Add(new Segment(window, tick, null));
        if (segments.Count > MaxSegments) segments.RemoveAt(0);
        return true;
    }

    internal Segment? Suggest(long now, LiveWindow? current, Func<LiveWindow, bool> alive) =>
        segments.AsEnumerable().Reverse().FirstOrDefault(s =>
            !Same(current, s.Window) && s.End is long end &&
            now - end <= LookbackSeconds * 1000L && s.Seconds(now) >= MinFocusSeconds &&
            alive(s.Window));

    internal LiveWindow? MarkedTarget(LiveWindow? current, Func<LiveWindow, bool> available) =>
        MarkedWindow is { } marked && !Same(current, marked) && available(marked) ? marked : null;

    internal LiveWindow? ReturnTarget(long now, LiveWindow? current, Func<LiveWindow, bool> available) =>
        MarkedTarget(current, available) ??
        (Suggest(now, current, available) ?? Recent(current, available).FirstOrDefault())?.Window;

    internal IEnumerable<Segment> Recent(LiveWindow? current, Func<LiveWindow, bool> available)
    {
        var seen = new HashSet<(nint Handle, int ProcessId, nint Identity)>();
        foreach (var segment in segments.AsEnumerable().Reverse())
        {
            var window = segment.Window;
            if (!Same(current, window) && seen.Add((window.Handle, window.ProcessId, window.Identity)) && available(window))
                yield return segment;
        }
    }

    internal void Mark(LiveWindow window) => MarkedWindow = window;

    internal void ConsumeMark(LiveWindow window)
    {
        if (Same(MarkedWindow, window)) MarkedWindow = null;
    }

    internal void Clear()
    {
        segments.Clear();
        MarkedWindow = null;
    }
}
