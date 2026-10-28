using PcHealth.Core.Advice;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Checks run side by side so a scan takes as long as its slowest check instead of the sum of
/// all of them. What must not change with that: the order results come back in, a failing
/// check failing on its own, cancellation, and the checks that measure speed or temperature
/// having the machine to themselves.
/// </summary>
public sealed class ScanOrchestratorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly string[] ListedOrder = ["slowest", "slow", "quick", "instant"];
    private static readonly int[] EachAloneCheckSawOnlyItself = [1, 1];

    private sealed class FakeModule(
        string id, Func<CancellationToken, Task>? body = null, bool runsAlone = false, bool runsAfterOthers = false)
        : IHealthModule
    {
        public ModuleManifest Manifest { get; } =
            new() { Id = id, Name = id, Description = id, Reads = [], RunsAlone = runsAlone, RunsAfterOthers = runsAfterOthers };

        public async Task<ModuleResult> RunAsync(ModuleContext context, IProgress<ModuleProgress>? progress, CancellationToken ct)
        {
            if (body is not null) await body(ct);
            return new ModuleResult { ModuleId = id, Status = ModuleStatus.Completed };
        }
    }

    /// <summary>Counts how many fake modules are inside their body at once.</summary>
    private sealed class Overlap
    {
        private int _now;
        public int Max;

        public async Task Hold(TimeSpan time, Action<int>? onEnter = null)
        {
            var now = Interlocked.Increment(ref _now);
            InterlockedMax(ref Max, now);
            onEnter?.Invoke(now);
            try { await Task.Delay(time); }
            finally { Interlocked.Decrement(ref _now); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private static ScanOrchestrator Orchestrator() => new(new VerdictEngine([]));

    private static ModuleContext Context()
    {
        var receipt = new ReceiptLog();
        return new ModuleContext { Processes = new ProcessRunner(receipt), Receipt = receipt, AppTempDir = Path.GetTempPath() };
    }

    [Fact]
    public async Task Checks_that_block_before_their_first_await_still_overlap()
    {
        // Each check blocks its thread until the other has started. Run one after another, the
        // first would wait for a second that never starts. This is the shape most real modules
        // have - synchronous WMI work inside an async method - so it is the case that matters.
        using var aStarted = new ManualResetEventSlim();
        using var bStarted = new ManualResetEventSlim();
        IHealthModule[] modules =
        [
            new FakeModule("a", ct => { aStarted.Set(); return bStarted.Wait(Patience, ct) ? Task.CompletedTask : throw new TimeoutException("b never started"); }),
            new FakeModule("b", ct => { bStarted.Set(); return aStarted.Wait(Patience, ct) ? Task.CompletedTask : throw new TimeoutException("a never started"); }),
        ];

        var results = await Orchestrator().RunModulesAsync(modules, Context());

        Assert.All(results, r => Assert.Equal(ModuleStatus.Completed, r.Status));
    }

    [Fact]
    public async Task Results_come_back_in_the_order_the_modules_were_given()
    {
        // The first-listed check finishes last; the report must not reorder because of it.
        IHealthModule[] modules =
        [
            new FakeModule("slowest", ct => Task.Delay(300, ct)),
            new FakeModule("slow", ct => Task.Delay(200, ct)),
            new FakeModule("quick", ct => Task.Delay(100, ct)),
            new FakeModule("instant"),
        ];

        var results = await Orchestrator().RunModulesAsync(modules, Context());

        Assert.Equal(ListedOrder, results.Select(r => r.ModuleId));
    }

    [Fact]
    public async Task A_check_that_runs_alone_never_shares_the_machine()
    {
        var overlap = new Overlap();
        var seenByAloneChecks = new List<int>();
        var hold = TimeSpan.FromMilliseconds(100);
        Func<CancellationToken, Task> shared = _ => overlap.Hold(hold);
        Func<CancellationToken, Task> alone = _ => overlap.Hold(hold, now => { lock (seenByAloneChecks) seenByAloneChecks.Add(now); });

        // The alone checks sit in the middle of the list on purpose: position must not matter.
        IHealthModule[] modules =
        [
            new FakeModule("s1", shared), new FakeModule("s2", shared),
            new FakeModule("bench", alone, runsAlone: true),
            new FakeModule("s3", shared), new FakeModule("s4", shared),
            new FakeModule("sensors", alone, runsAlone: true),
        ];

        await Orchestrator().RunModulesAsync(modules, Context());

        Assert.Equal(EachAloneCheckSawOnlyItself, seenByAloneChecks);
        Assert.True(overlap.Max >= 2, "the shared checks never overlapped, so this test proved nothing");
    }

    [Fact]
    public async Task The_admin_helper_is_awaited_after_the_shared_checks_and_before_the_benchmarks()
    {
        // The scan entry points pass the elevated helper's run as the hook: it may overlap the
        // shared checks, but a benchmark must never start while it is still going.
        var order = new List<string>();
        void Note(string what) { lock (order) order.Add(what); }
        IHealthModule[] modules =
        [
            new FakeModule("shared-1", async ct => { await Task.Delay(60, ct); Note("shared-1"); }),
            new FakeModule("bench", _ => { Note("bench"); return Task.CompletedTask; }, runsAlone: true),
            new FakeModule("shared-2", async ct => { await Task.Delay(20, ct); Note("shared-2"); }),
        ];

        await Orchestrator().RunModulesAsync(modules, Context(),
            beforeRunsAlone: async () => { await Task.Delay(40); Note("helper finished"); return []; });

        Assert.Equal(4, order.Count);
        Assert.True(order.IndexOf("helper finished") > order.IndexOf("shared-1"));
        Assert.True(order.IndexOf("helper finished") > order.IndexOf("shared-2"));
        Assert.Equal("bench", order[^1]);
    }

    [Fact]
    public async Task A_check_that_runs_alone_sees_what_the_others_and_the_admin_helper_found()
    {
        // The disk benchmark must not write to a drive this scan already calls failing, and on an
        // unelevated scan the drive's own health is read only by the helper (REMEDIES-PLAN H2).
        static Metric Reading(string id, double value, int disk) =>
            new(id, id, value, HealthCategory.Storage, Subject: "drive", DiskNumber: disk);
        var engine = new VerdictEngine([new Rule
        {
            Id = "r", MetricId = "smart.reallocated",
            Bands = [new Band { Grade = "Good", Lte = 0 }, new Band { Grade = "Bad", Always = true }],
        }]);
        IReadOnlyList<Verdict>? seen = null;
        var context = Context();
        IHealthModule[] modules =
        [
            new ResultModule("events", Reading("events.disk_errors_90d", 3, 0)),
            new FakeModule("bench", _ => { seen = context.EarlierFindings; return Task.CompletedTask; }, runsAlone: true),
        ];
        IReadOnlyList<ModuleResult> fromHelper =
            [new ModuleResult { ModuleId = "disk.smart", Status = ModuleStatus.Completed, Metrics = [Reading("smart.reallocated", 5, 1)] }];

        await new ScanOrchestrator(engine).RunModulesAsync(modules, context,
            beforeRunsAlone: () => Task.FromResult(fromHelper));

        Assert.NotNull(seen);
        Assert.Contains(seen, v => v.Metric.Id == "events.disk_errors_90d");
        var smart = Assert.Single(seen, v => v.Metric.Id == "smart.reallocated");
        Assert.Equal(Grade.Bad, smart.Grade);
        Assert.Equal(1, smart.Metric.DiskNumber);
    }

    [Fact]
    public async Task A_check_that_runs_after_others_starts_after_the_admin_helper_and_sees_what_it_found()
    {
        // The file-backup check concludes from the shadow copies only the helper can count
        // (REMEDIES-PLAN Step A row 7), so it must not start beside the others.
        var order = new List<string>();
        void Note(string step) { lock (order) order.Add(step); }
        IReadOnlyList<Verdict>? seen = null;
        var context = Context();
        IHealthModule[] modules =
        [
            new FakeModule("backup.files", _ => { Note("backup.files"); seen = context.EarlierFindings; return Task.CompletedTask; },
                runsAfterOthers: true),
            new FakeModule("shared", _ => { Note("shared"); return Task.CompletedTask; }),
        ];
        IReadOnlyList<ModuleResult> fromHelper =
        [
            new ModuleResult
            {
                ModuleId = "backup.posture", Status = ModuleStatus.Completed,
                Metrics = [new Metric("backup.shadow_copies", "Shadow copies", 0, HealthCategory.Storage, InfoOnly: true)],
            },
        ];

        var results = await Orchestrator().RunModulesAsync(modules, context,
            beforeRunsAlone: async () => { await Task.Delay(40); Note("helper finished"); return fromHelper; });

        Assert.Equal(["shared", "helper finished", "backup.files"], order);
        Assert.Equal(0d, Assert.Single(seen!, v => v.Metric.Id == "backup.shadow_copies").Metric.Value.Number);
        Assert.Equal(["backup.files", "shared"], results.Select(r => r.ModuleId)); // still in the order given
    }

    [Fact]
    public void A_check_that_runs_beside_others_is_handed_no_findings_at_all() =>
        // Set only between the two phases: a check running beside others cannot know what they found.
        Assert.Empty(Context().EarlierFindings);

    private sealed class ResultModule(string id, Metric metric) : IHealthModule
    {
        public ModuleManifest Manifest { get; } = new() { Id = id, Name = id, Description = id, Reads = [] };

        public Task<ModuleResult> RunAsync(ModuleContext context, IProgress<ModuleProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new ModuleResult { ModuleId = id, Status = ModuleStatus.Completed, Metrics = [metric] });
    }

    [Fact]
    public async Task No_more_than_the_cap_run_at_once()
    {
        var overlap = new Overlap();
        var modules = Enumerable.Range(0, 12)
            .Select(i => (IHealthModule)new FakeModule($"m{i}", _ => overlap.Hold(TimeSpan.FromMilliseconds(80))))
            .ToList();

        await Orchestrator().RunModulesAsync(modules, Context());

        Assert.InRange(overlap.Max, 2, ScanOrchestrator.MaxConcurrentModules);
    }

    [Fact]
    public async Task A_check_that_throws_fails_on_its_own()
    {
        IHealthModule[] modules =
        [
            new FakeModule("fine-1", ct => Task.Delay(50, ct)),
            new FakeModule("broken", _ => throw new InvalidOperationException("WMI said no")),
            new FakeModule("fine-2", ct => Task.Delay(50, ct)),
        ];

        var results = await Orchestrator().RunModulesAsync(modules, Context());

        Assert.Equal(ModuleStatus.Completed, results[0].Status);
        Assert.Equal(ModuleStatus.Failed, results[1].Status);
        Assert.Contains("WMI said no", results[1].Error);
        Assert.Equal(ModuleStatus.Completed, results[2].Status);
    }

    [Fact]
    public async Task Cancelling_stops_the_scan_instead_of_reporting_it_finished()
    {
        // Cancelled from outside on a timer. A first version had one fake check cancel while the
        // other waited, which hangs for ever whenever only one check can run at a time.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        IHealthModule[] modules =
        [
            new FakeModule("waits-forever", ct => Task.Delay(Timeout.Infinite, ct)),
            new FakeModule("never-reached-or-quick"),
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Orchestrator().RunModulesAsync(modules, Context(), ct: cts.Token));
    }

    [Fact]
    public async Task Progress_names_a_check_that_is_still_running()
    {
        var reports = new List<ScanProgress>();
        var progress = new SyncProgress(p => { lock (reports) reports.Add(p); });
        IHealthModule[] modules =
        [
            new FakeModule("slow", ct => Task.Delay(300, ct)),
            new FakeModule("quick"),
        ];

        await Orchestrator().RunModulesAsync(modules, Context(), progress);

        // Once "quick" has finished, the step moves to 2 of 2 and the name points at "slow".
        Assert.Contains(reports, r => r is { ModuleId: "slow", Index: 2, Total: 2 });
        Assert.All(reports, r => Assert.InRange(r.Index, 1, 2));
    }

    [Theory]
    [InlineData("1.0.0-rc.1+25a2cd562d932ac9cba4223d129d611f5053fae6", "1.0.0-rc.1+25a2cd562d932ac9cba4223d129d611f5053fae6")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1")]
    [InlineData(null, "1.2.3")]
    [InlineData("  ", "1.2.3")]
    public void Reports_name_the_exact_build(string? informational, string expected) =>
        // Acceptance row A5: the report's version must match the exe exactly, commit included.
        Assert.Equal(expected, ScanOrchestrator.DescribeVersion(informational, new Version(1, 2, 3, 0)));

    [Fact]
    public void A_build_without_any_version_says_dev() =>
        Assert.Equal("dev", ScanOrchestrator.DescribeVersion(null, null));

    // --- The advice section (STEP-B-SPEC 3.1): worked out once, after the grades ----------------------

    private const string ModelA = "MODEL-A — 240 GB", ModelB = "MODEL-B — 1000 GB";

    private static readonly VerdictEngine ReallocatedRule = new([new Rule
    {
        Id = "r", MetricId = "smart.reallocated",
        Bands = [new Band { Grade = "Good", Lte = 0 }, new Band { Grade = "Bad", Always = true }],
    }]);

    private static ScanReport Build(AdviceEngine? advice, double reallocatedOnA, IReadOnlyList<TrendFinding>? trends = null)
    {
        IReadOnlyList<ModuleResult> results =
        [
            new ModuleResult
            {
                ModuleId = "disk.smart", Status = ModuleStatus.Completed,
                Metrics =
                [
                    new Metric("smart.reallocated", "Reallocated sectors", reallocatedOnA, HealthCategory.Storage, Subject: ModelA, DiskNumber: 0),
                    new Metric("smart.reallocated", "Reallocated sectors", 0, HealthCategory.Storage, Subject: ModelB, DiskNumber: 1),
                ],
            },
            new ModuleResult
            {
                ModuleId = "sys.overview", Status = ModuleStatus.Completed,
                Metrics = [new Metric("os.uptime_days", "Time since last restart", 3.2, HealthCategory.SoftwareOs, Unit: "days")],
            },
        ];
        return new ScanOrchestrator(ReallocatedRule, advice).BuildReport(results, [], ranElevated: false, trends: trends);
    }

    [Fact]
    public void A_report_carries_an_advice_section_and_its_schema_version_says_so()
    {
        // A healthy drive and every module complete: whatever items exist, there is nothing to suggest.
        var report = Build(advice: null, reallocatedOnA: 0);

        Assert.Equal("1.1", report.SchemaVersion);
        Assert.NotNull(report.Advice);
        Assert.Empty(report.Advice.Recommendations);
        Assert.Empty(report.Advice.NotMeasured);
        Assert.Equal(0, report.Advice.OtherFindings);
        Assert.Empty(report.Advice.ItemErrors);
    }

    [Fact]
    public void Advice_never_changes_a_grade_or_a_module_status()
    {
        IReadOnlyList<TrendFinding> trends =
            [new TrendFinding(TrendKind.CounterGrowth, "smart.reallocated", ModelA, "Reallocated sectors", Grade.Bad, "grew", 3, 5,
                DateTimeOffset.Now.AddDays(-7), [])];
        var throwing = new TestAdviceItem("B", "security", (_, _) => throw new InvalidOperationException());

        var without = Build(AdviceEngine.None, 5, trends);
        var builtIn = Build(advice: null, 5, trends);
        var busy = Build(new AdviceEngine([TestAdviceItem.CardPerFailingDrive(), throwing]), 5, trends);

        Assert.Null(without.Advice);
        Assert.Single(busy.Advice!.Recommendations);
        Assert.Single(busy.Advice.ItemErrors);
        foreach (var report in new[] { builtIn, busy })
        {
            Assert.Equal(without.Verdicts.Select(v => (v.Metric.Id, v.Metric.Subject, v.Grade)), report.Verdicts.Select(v => (v.Metric.Id, v.Metric.Subject, v.Grade)));
            Assert.Equal(without.CategoryGrades.OrderBy(kv => kv.Key), report.CategoryGrades.OrderBy(kv => kv.Key));
            Assert.Equal(Grade.Bad, report.OverallGrade);
            Assert.Equal(without.OverallGrade, report.OverallGrade);
            Assert.Equal(without.Modules.Select(m => (m.ModuleId, m.Status)), report.Modules.Select(m => (m.ModuleId, m.Status)));
        }
    }

    [Fact]
    public void A_report_on_an_OS_with_no_advice_has_no_section() =>
        Assert.Null(Build(AdviceEngine.ForOs("linux"), 5).Advice);

    /// <summary>Progress&lt;T&gt; posts to the thread pool, so a test reading it could miss reports.</summary>
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
