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
Require(t.Suggest(49_500, b, _ => true)?.Window == renamed, "45-second source must survive a detour and dashboard.");
Require(t.Suggest(49_500, b, window => window == b) is null, "Must skip stale windows.");
Require(t.Suggest(946_001, b, _ => true) is null, "Expired candidate must not be suggested.");
t.Observe(renamed, 50_000);
Require(t.Suggest(50_000, renamed, _ => true) is null, "Must not suggest current task back to itself.");

var noGap = new Trail();
noGap.Observe(a, 0);
noGap.Observe(null, 35_000); // idle/suspend boundary
noGap.Observe(a, 600_000);
noGap.Observe(b, 620_000);
Require(noGap.Suggest(620_000, b, _ => true) is null, "Must not count an idle gap as dwell.");
for (int i = 0; i < 100; i++) noGap.Observe(i % 2 == 0 ? a : b, 621_000 + i);
Require(noGap.Segments.Count == Trail.MaxSegments, "Trail must stay bounded.");

var recent = new Trail();
recent.Observe(a, 0);
recent.Observe(b, 100);
recent.Observe(renamed, 200);
recent.Observe(successor, 300);
Require(recent.Recent(successor, _ => true).Select(s => s.Window).SequenceEqual([renamed, b]),
    "Quick switch must include brief visits, deduplicate by live identity, and exclude the current window.");
Require(recent.Recent(null, window => window != renamed).Select(s => s.Window).SequenceEqual([successor, b]),
    "Closed targets must be skipped, including older snapshots for the same identity.");

var priority = new Trail();
priority.Observe(a, 0);
priority.Observe(b, 60_000);
priority.Observe(successor, 60_500);
Require(priority.ReturnTarget(61_000, successor, _ => true) == a, "Earlier sustained work must beat a brief detour.");
priority.Mark(b);
Require(priority.ReturnTarget(61_000, successor, _ => true) == b, "Mark must beat sustained work and be used directly.");
Require(priority.ReturnTarget(61_000, b, _ => true) == a && priority.MarkedWindow == b,
    "Pressing return in the marked window must skip and retain the mark.");
Require(priority.ReturnTarget(61_000, successor, window => window != b) == a && priority.MarkedWindow == b,
    "An unavailable mark must fall through without being consumed.");
priority.Mark(renamed);
priority.ConsumeMark(b);
Require(priority.MarkedWindow == renamed, "A later mark replaces the previous window and survives other jumps.");
priority.ConsumeMark(a);
Require(priority.MarkedWindow is null, "A confirmed return to the exact marked window consumes the mark despite title changes.");
Require(recent.ReturnTarget(400, successor, _ => true) == renamed, "Very brief visits remain an immediate fallback.");
priority.Mark(b);
priority.Clear();
Require(priority.MarkedWindow is null && priority.Segments.Count == 0, "Clearing the session must clear the mark.");

Console.WriteLine("PASS: direct return order, exact identity, idle gaps, retention, and mark lifecycle.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
