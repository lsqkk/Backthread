namespace Backthread;

internal sealed record Segment(LiveWindow Window, long Start, long? End)
{
    internal long Seconds(long now) => Math.Max(0, ((End ?? now) - Start) / 1000);
}

internal sealed record Checkpoint(LiveWindow Window, string Note, DateTimeOffset SavedAt);

// Timestamps here are monotonic. Neither this model nor the desktop layer writes to disk.
internal sealed class Trail
{
    internal const int MinFocusSeconds = 45;
    internal const int MaxSegments = 60;
    internal const int MaxCheckpoints = 12;
    internal const int LookbackSeconds = 900;
    private readonly List<Segment> segments = [];
    private readonly List<Checkpoint> checkpoints = [];
    internal IReadOnlyList<Segment> Segments => segments;
    internal IReadOnlyList<Checkpoint> Checkpoints => checkpoints;
    internal LiveWindow? LastExternal { get; private set; }

    internal static bool Same(LiveWindow? a, LiveWindow? b) =>
        a is not null && b is not null && a.Handle == b.Handle &&
        a.ProcessId == b.ProcessId && a.Identity == b.Identity;

    internal bool Observe(LiveWindow? window, long tick)
    {
        var tail = segments.LastOrDefault();
        if (window is not null && tail is { End: null } && Same(tail.Window, window))
        {
            LastExternal = window;
            if (tail.Window == window) return false;
            segments[^1] = tail with { Window = window };
            return true;
        }
        bool ended = tail is { End: null };
        if (ended) segments[^1] = tail! with { End = Math.Max(tail!.Start, tick) };
        if (window is null) return ended;
        LastExternal = window;
        segments.Add(new Segment(window, tick, null));
        if (segments.Count > MaxSegments) segments.RemoveAt(0);
        return true;
    }

    internal Segment? Suggest(long now, Func<LiveWindow, bool> alive) =>
        segments.AsEnumerable().Reverse().FirstOrDefault(s =>
            !Same(LastExternal, s.Window) && s.End is long end &&
            now - end <= LookbackSeconds * 1000L && s.Seconds(now) >= MinFocusSeconds &&
            alive(s.Window));

    internal bool Push(LiveWindow window, string note, DateTimeOffset now)
    {
        if (checkpoints.Count >= MaxCheckpoints) return false;
        checkpoints.Add(new Checkpoint(window, note.Trim(), now));
        return true;
    }

    internal void Pop()
    {
        if (checkpoints.Count != 0) checkpoints.RemoveAt(checkpoints.Count - 1);
    }

    internal void Clear()
    {
        segments.Clear();
        checkpoints.Clear();
        LastExternal = null;
    }
}
