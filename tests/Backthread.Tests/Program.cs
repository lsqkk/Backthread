using Backthread;

var a = new LiveWindow(101, 4, 1, "Report", "Editor");
var renamed = a with { Title = "Report (updated)" };
var b = new LiveWindow(102, 4, 2, "Detour", "Editor");
var successor = a with { Identity = 3 };

Require(!Trail.Same(a, successor), "Reused HWND and PID must not match.");

var t = new Trail();
t.Observe(a, 1_000);
t.Observe(renamed, 2_000);
Require(t.Segments.Count == 1 && t.Segments[0].Window == renamed, "Title change must not reset dwell.");
t.Observe(b, 46_000);
t.Observe(null, 49_500);
Require(t.Suggest(49_500, _ => true)?.Window == renamed, "45-second source must survive a detour and dashboard.");
Require(t.Suggest(49_500, window => window == b) is null, "Must skip stale windows.");
Require(t.Suggest(946_001, _ => true) is null, "Expired candidate must not be suggested.");
t.Observe(renamed, 50_000);
Require(t.Suggest(50_000, _ => true) is null, "Must not suggest current task back to itself.");

var noGap = new Trail();
noGap.Observe(a, 0);
noGap.Observe(null, 35_000); // idle/suspend boundary
noGap.Observe(a, 600_000);
noGap.Observe(b, 620_000);
Require(noGap.Suggest(620_000, _ => true) is null, "Must not count an idle gap as dwell.");
for (int i = 0; i < 100; i++) noGap.Observe(i % 2 == 0 ? a : b, 621_000 + i);
Require(noGap.Segments.Count == Trail.MaxSegments, "Trail must stay bounded.");

var savedAt = DateTimeOffset.UnixEpoch;
Require(t.Push(a, " review appendix ", savedAt), "Must save first checkpoint.");
Require(t.Push(b, "answer message", savedAt), "Must save nested checkpoint.");
Require(t.Checkpoints[^1].Window == b, "Nested checkpoint must be on top.");
t.Pop();
Require(t.Checkpoints[^1].Window == a && t.Checkpoints[^1].Note == "review appendix", "Pop must reveal previous checkpoint.");
for (int i = 1; i < Trail.MaxCheckpoints; i++) Require(t.Push(b, "", savedAt), "Must accept up to the limit.");
Require(!t.Push(a, "overflow", savedAt), "Overflow must not silently discard older notes.");
t.Clear();
Require(t.Checkpoints.Count == 0 && t.Segments.Count == 0 && t.LastExternal is null, "Clear must forget all references.");

Console.WriteLine("PASS: trail suggestions, exact identity, idle gaps, retention, and stack capacity.");
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
