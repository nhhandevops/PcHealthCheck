# Phase 1 Step B: build spec — the recommendation engine, advice only

Status: design, 2026-10-28, merged from two designs by a judge who checked every code claim. HEAD
`394cbab`. Nothing on the PC changes in Step B. Grades, category grades, the overall grade, the
kit's verdicts and the fleet table do not move.

Plan references: Step B row `docs/REMEDIES-PLAN.md:842-848`. "Two uses": `:16-29`. Language: `:202-221`.
Principles 2, 3, 10, 12, 13 and 14: `:283-320`. Items: S1 `:578`, S4 `:632`, H1 `:658-685`, O1 `:767`,
O3 `:787`, O4 `:799`, D4-D9 `:403-472`, M5/M6 `:555-571`. Phase 2 gate: `:882-888`. Decisions 3, 6, 9
and 13: `:965, 968, 974, 984`.

---

## 0. What Step B ships, in one screen

- A **"What you can do"** section in the JSON, the HTML report, the GUI and the headless console. All
  four surfaces show the same section, worked out once in `ScanOrchestrator.BuildReport`.
- **Live items:** H1 (failing or worn internal drive, three urgencies), O4 (security switched off),
  O1 (Windows 11 blocked only by a firmware switch), one **low-space card for the system drive**
  carrying the D4-D9 advice, O3 (hot *and* slow), and S1 (Restart, not Shut down) with S4's
  "will not fix a slow start" label.
- **Catalog text only, with no trigger code:** M5, M6, and S4's hard-disk and low-memory variants.
  Their readings are Step C rows 1, 3 and 7. The plan's Step B row carries "the advice text of …
  M5 and M6" (`REMEDIES-PLAN.md:847`).
- **One module change:** VolumesModule reports two information-only rows per attached disk, its
  role (system, internal or external) and the space in use. Without them H1 cannot tell the laptop's
  SSD from the auditor's USB drive, whose read errors are still graded Bad
  (`docs/warning_bug_and_solutions.md:447-449`).
- **The cap:** at most 5 cards, then "N more". "Don't show again" works in the GUI only. Reports
  and exports always list everything.
- **Text:** every line is a catalog key plus typed arguments, rendered in English from
  `src/PcHealth.Core/Text/en.json`. Step E adds `vi.json`.

---

## 1. Invariants (each one has a test, section 7)

1. **A recommendation exists only because of a graded problem.** Every recommendation carries at
   least one piece of *problem evidence*:
   - a verdict that is not InfoOnly and is graded Normal or Bad; or
   - a TrendFinding graded Normal or Bad.

   InfoOnly rows grade Unknown (`VerdictEngine.cs:44-45`) and can never be problem evidence. Value
   tests (uptime, RAM) may only *narrow* a trigger. The `Recommendation` factory rejects a card
   with no problem evidence, and the engine records that as an item error (3.7). This is plan
   principle 2 (`REMEDIES-PLAN.md:283-286`), enforced in code.
2. **A projection trend can be Good.** `TrendAnalyzer.BuildProjection` returns Good findings when
   the limit is 730 or more days away (`TrendAnalyzer.cs:250-255`, added at `:147-148`). A trend is
   therefore problem evidence only when its own Grade is Normal or Bad.
3. **Advice never changes grading.** BuildReport computes Verdicts, CategoryGrades and OverallGrade
   exactly as today (`ScanOrchestrator.cs:189-220`), and Advice is added afterwards.
4. **"Tried and broke" and "deliberately not checked" stay apart** (CLAUDE.md rules 1-2):
   - a needed module that is Failed or PartiallyCompleted *and* left the trigger readings missing
     gives a **Not measured** line;
   - a Skipped or absent module gives nothing, or at most a grey Note;
   - neither changes a module status or the "could not finish" banner.
5. **Never quote a metric's Detail or a trend's Message.** Advice copies neither:
   - Details can carry folder paths (`SecurityPostureModule.cs:179-186`);
   - volume Subjects can carry a personal volume label (`VolumesModule.cs:51`);
   - trend Messages are current-culture formatted (`TrendAnalyzer.cs:258-264, 294-296`: `{x:0.0}`,
     `{reached:MMMM yyyy}`).

   Every number comes from typed values.
6. **English is the data language.** JSON `Text`, the headless console and the kit stay English
   (`REMEDIES-PLAN.md:204-206`).

---

## 2. Data model

### 2.1 New Core types (`src/PcHealth.Core/Advice/AdviceModel.cs`, namespace `PcHealth.Core.Advice`)

```csharp
public enum AdviceUrgency { ActNow, CheckAgain, Plan, Advice }
public enum AdviceLineRole { Title, Finding, Why, Do, DoNot, ForIt }
public enum AdviceUnit { None, Count, Percent, Gb, Minutes, Days, Celsius, Date }

public readonly record struct AdviceArg(double? Number, string? Text, AdviceUnit Unit);   // exactly one of Number/Text
public sealed record AdviceText(AdviceLineRole Role, string Key,
    IReadOnlyDictionary<string, AdviceArg> Args, string Text);                          // Text = English rendering
public sealed record AdviceEvidence(string MetricId, string? Subject, int? DiskNumber,
    Grade Grade, TrendKind? Trend);                                                      // reference only, never Detail
public sealed record Recommendation(
    string Id,            // "H1:disk0", "H1:unknown-1", "O4", "SPACE:C:" — never a model name
    string Item,          // plan id: "H1", "O4", "O1", "SPACE", "O3", "S1"
    string Area,          // drive-health | security | windows | space | heat | startup
    AdviceUrgency Urgency, Grade Severity, int Rank,
    string? Subject, int? DiskNumber,
    AdviceText Title, IReadOnlyList<AdviceText> Lines, IReadOnlyList<AdviceEvidence> Evidence)
{ [JsonIgnore] public string Fingerprint { get; init; } = ""; }
public sealed record NotMeasured(string Area, IReadOnlyList<string> ModuleIds, AdviceText Text);
public sealed record AdviceSection(
    IReadOnlyList<Recommendation> Recommendations,   // full list, display order, never capped
    IReadOnlyList<NotMeasured> NotMeasured,
    IReadOnlyList<AdviceText> Notes,                 // deliberate non-checks, grey
    int OtherFindings,                               // graded Normal/Bad readings no card used
    IReadOnlyList<AdviceText> ItemErrors);           // an item that threw; empty on every test fixture
```

- `Severity` is the worst grade in the evidence.
- `Fingerprint` is SHA-256 over: Item, Urgency, DiskNumber, Subject, and the sorted evidence
  (MetricId, Subject, Grade, TrendKind). For H1 only, each damage counter's integer value is
  included too, because those counters only ever grow.

### 2.2 Report change

- `ScanReport` (`ScanReport.cs:17-37`) gains `public AdviceSection? Advice { get; init; }`. It is not
  `required`, so every `new ScanReport{…}` in tests still compiles (`ReportExportTests.cs:21`).
  - Null means advice was not worked out: an older report, a hand-built test report, or an OS with
    no advice items (Linux).
  - BuildReport always sets it on Windows.
- `SchemaVersion` changes from "1.0" to "1.1" (`ScanReport.cs:19`). Nothing reads it: the only other
  hit is a kit fixture, `kit-tests.ps1:80`.
- JSON is written by the unchanged exporter (`JsonReportExporter.cs:9-14`): PascalCase, enums as
  strings, nulls omitted. Example:

```json
"Advice": {
  "Recommendations": [ {
    "Id": "H1:disk0", "Item": "H1", "Area": "drive-health", "Urgency": "ActNow", "Severity": "Bad",
    "Rank": 10, "Subject": "<model> — 240 GB", "DiskNumber": 0,
    "Title": { "Role": "Title", "Key": "advice.h1.title.act-now.system", "Args": {},
               "Text": "Back up now: the drive Windows runs from is failing" },
    "Lines": [ { "Role": "Finding", "Key": "advice.h1.evidence.smart.reallocated",
                 "Args": { "count": { "Number": 5, "Unit": "Count" } },
                 "Text": "It has retired 5 damaged areas so far." } ],
    "Evidence": [ { "MetricId": "smart.reallocated", "Subject": "<model> — 240 GB", "DiskNumber": 0, "Grade": "Bad" } ]
  } ],
  "NotMeasured": [], "Notes": [], "OtherFindings": 4, "ItemErrors": []
}
```

- Args use the `Number`/`Text` shape of `Metric.Value` (`Metrics.cs:30-43`), so a PowerShell reader
  writes `.Args.count.Number`, the same way the kit reads `.Metric.Value.Number`
  (`audit-common.ps1:705, 745`).
- **The stable contract for Step D** is Item, Urgency, Key and the Arg names, never `Text`
  (KIT-005).

### 2.3 Compatibility, verified

**The kit** reads only these report fields:
- `Verdicts` (`audit-common.ps1:522`);
- `Modules` and `RanElevated` (`:631-640`);
- `MachineName` (`:877`);
- `OverallGrade` (`:888`);
- `AppVersion` (`:894`).

It filters verdicts by exact ids: DriveHealthIds (`:163-168`), `smart.*` and `storage.wear_pct` for
"drive read" (`:653`), `storage.volume.free_pct` (`:744`), the Windows 11 ids (`:761-769`), and
`ram.total_gb`, battery, os, heat and bugcheck (`:704-859`).
- The new ids `storage.disk.role` and `storage.disk.used_gb` match none of these.
- An extra top-level `Advice` property is ignored by `ConvertFrom-Json`.
- Old zips are unaffected (`Get-JsonField`, `:37-42`).

**The fleet summary** copies the HTML and never parses it (`summarize-fleet.ps1:71-72`).

**The daily smoke** reads `Modules`, `OverallGrade`, `RanElevated`, `Receipt`, the HTML line
"Network connections made by PcHealthCheck during this scan: 0." and the console's em dash
(`daily-smoke.ps1:299-324`). All of them are untouched.

**The elevated worker** sends ModuleResults, not reports, so the IPC is untouched.

### 2.4 New information-only rows from VolumesModule (work group 2)

Both rows are built from reads the module already makes: `DriveMap.Read`, which reads MSFT_Disk and
MSFT_Partition (`DiskBus.cs:84-113`), and the volume sizes (`VolumesModule.cs:53-58`). No WMI query
is added. The consent row already says "Which disk each volume is on, and each disk's connection and
type" (`VolumesModule.cs:16`). The capability hash does not include Reads (`Contracts.cs:83-97`), so
no re-consent is triggered.

**`storage.disk.role`** (Category Storage, InfoOnly, Label "Disk role"), one row per attached disk:
- Value Text, `DiskNumber` = MSFT_Disk.Number. Its Subject is the disk's drive letters ("C:, D:"), or
  "Disk N" when it has none. It never carries a volume label.
- `system`: `IsBoot`, or the disk holds the Windows drive letter (`DiskBus.cs:75-77, 112`).
- `external`: `DiskBus.IsExternal(BusType)` (`DiskBus.cs:22`), unless the disk is `system`.
- `internal`: everything else, unknown buses included (`DiskBus.cs:10-13`, KIT-003 direction).
- Detail: "Inside this PC" / "Plugged in over USB" (from `DiskBus.Describe`, `:25-30`), "holds C:, D:".

**`storage.disk.used_gb`** (InfoOnly, Unit "GB"), with the same DiskNumber and Subject:
- The sum of Size − SizeRemaining over that disk's lettered volumes, in GiB as `VolumeReadings`
  computes it (`VolumeReadings.cs:21-22`), rounded to 0.1.
- Emitted only when every lettered partition the map puts on that disk had a measured volume. A
  partial sum must never read as a small number.
- Label "Space in use". Detail: "Added up over this disk's drives (C:), Windows and programs
  included." on the `system` disk only; every other disk's says "Added up over this disk's drives
  (F:)." The HTML report prints the detail, and the auditor's USB drive holds no Windows (review
  round, 2026-10-28).

Further rules:
- When the drive map cannot be read, neither row is written. The existing Note stays
  (`VolumesModule.cs:31-36`), and the module status does not change.
- `DriveMap` gains a public `DiskOf(string letter)` accessor; `_diskByLetter` is private today
  (`DiskBus.cs:58-59`). The row-building logic goes in a pure helper, `DiskRoles.From(DriveMap,
  measuredVolumes, systemDrive)`, tested without WMI (HANDOFF.md:139).
- History stores numbers only (`HistoryStore.cs:98-100`), so `used_gb` gains a series and `role`
  does not. That is harmless.
- PRIVACY.md already says the app reads which disk each letter is on and its connection
  (`PRIVACY.md:38`). It gains one line, in the same commit: "The report also lists, per disk, whether
  it is the system disk, inside the PC or external, and the total space in use on its lettered
  drives."

### 2.5 `DriveDamage` moves to Core

`FailingDrives.DamageReadings` (`FailingDrives.cs:27-32`, private) becomes
`PcHealth.Core.Metrics.DriveDamage.MetricIds`. `FailingDrives` then reads it from there. H1 and the
benchmark gate (H2) therefore agree by construction. The set is: `smart.predict_failure`,
`smart.attributes_at_threshold`, `smart.reallocated`, `smart.pending_sectors`,
`smart.offline_uncorrectable`, `smart.reported_uncorrectable`, `storage.read_errors_uncorrected`,
`storage.write_errors_uncorrected`.

---

## 3. Engine

### 3.1 Where it runs

- `AdviceEngine.Derive(IReadOnlyList<Verdict>, IReadOnlyList<TrendFinding>, IReadOnlyList<ModuleRunSummary>, AdviceContext) → AdviceSection?`
  - It is pure, takes plain records, and touches no OS (HANDOFF.md:139).
  - `AdviceContext(string? SystemDrive, bool RanElevated = false)`: BuildReport passes the Windows
    folder's root, normalized to "C:" (the same source as `DiskBus.cs:112`), and the report's own
    `RanElevated`. Tests and the replay pass their own.
- `ScanOrchestrator(VerdictEngine engine, AdviceEngine? advice = null)` (today
  `ScanOrchestrator.cs:20`). The default is the embedded item list for `RulesLoader.CurrentOs`
  (`Rules.cs:79-83`). The call sites `HeadlessScan.cs:126`, `MainWindowViewModel.cs:194`,
  `ScanOrchestratorTests.cs:172` and `LiveLinuxTests.cs:34` do not change.
- BuildReport calls `Derive` after the trends are merged (`ScanOrchestrator.cs:192-203`) and sets
  `Advice`. Doing it here gives the GUI, the headless scan and every export one section. The
  GUI/headless split once shipped a bug (`HeadlessScan.cs:166-172`).
- On an OS with no advice items, `Derive` returns null, and every surface omits the section.

### 3.2 Items are small C# classes; the text is data

- Each item is an `IAdviceItem` with `Id`, `Area`, `Os = "windows"`, `Rank` and
  `Evaluate(AdviceFacts, IReadOnlyList<Recommendation> earlier)`. It returns zero or more
  recommendations.
- Items live in `src/PcHealth.Core/Advice/Items/`. The engine runs them in a fixed order:
  H1, O4, O1, SPACE, O3, S1. S1 runs last because it reads H1's outcome for S4.
- **Why C# and not a JSON rule language:** a recommendation joins disks, volumes, modules and
  trends, and a JSON language for joins needs its own interpreter, loader checks and tests. The
  thresholds are mostly the existing rule grades. The only new numbers are S1's 7 days, the
  replacement factor and sizes, and the copy rates. They live in one `AdviceThresholds` static class
  with comments, and each one is tested on both sides.

### 3.3 Facts index (`AdviceFacts`, built once)

- Verdicts are indexed by id, by (id, Subject) and by DiskNumber. Trends are indexed by
  (MetricId, Subject), and modules by id.
- **Disk groups for H1.** The key is the verdict's DiskNumber (`Metrics.cs:53-62`; set by
  `DiskSmartModule.cs:143-147, 191-194` and `StorageReliabilityModule.cs:90, 111, 167`).
  - A damage verdict with a null DiskNumber gets its own group, keyed by Subject, with role
    `unknown`.
  - A trend has no DiskNumber (`TrendAnalyzer.cs:18-31`). It joins the drive whose verdict has the
    same MetricId and Subject, and only when no other drive in the scan has one. Two drives of one
    model share a Subject, so history keeps one series for both, and `TrendAnalyzer` compares a
    reading with the last sample stored, which may be the other drive's. Such a trend joins no
    drive and makes no group of its own: it is left to its own row, and counts in OtherFindings.
    Each such drive's group says it shares its history (`SharesHistory`), so H1 does not promise
    that a later scan will tell (review round, 2026-10-28; was "the one whose Value equals the
    trend's CurrentValue, and otherwise the Subject group", which made "grown from 0 to 2" out of
    two drives at 2 and 0).
  - A group's role comes from the `storage.disk.role` row with the same DiskNumber. With no row the
    role is `unknown`, which counts as internal (`audit-common.ps1:170-174`, `DiskBus.cs:10-13`).
- **The system volume:** a non-InfoOnly `storage.volume.free_pct` verdict whose Subject equals the
  context's SystemDrive, or starts with it plus a space ("C: (label)"), compared ordinal and
  ignoring case. This is the kit's rule (`audit-common.ps1:744`). Only the drive letter is ever
  printed, never the Subject.

### 3.4 Ordering, de-duplication, S4

| Rank | Card |
|---|---|
| 10 | H1 act now |
| 12 | H1 check again |
| 20 | H1 plan (worn) |
| 25 | O3 hot and slow |
| 30 / 31 | M6 / M5 (reserved for Step C, not built) |
| 40 | O4 security off |
| 45 | SPACE (low system drive) |
| 50 | O1 Windows 11 firmware switch (for IT) |
| 60 | S1 restart |

- Hardware and data come first (`REMEDIES-PLAN.md:287`, Step B row "hardware blockers first").
- Ties sort by Severity (Bad before Normal), then DiskNumber, then Subject (ordinal). The order is
  deterministic.
- **De-duplication:**
  - one H1 card per disk group, however many readings it has;
  - one O4 card listing every setting that is off;
  - one SPACE card.
- **Principle 3:** when H1 fires (any urgency) on a disk whose role is `system` or `unknown`, the
  SPACE card is dropped. H1 instead gets the also-low-space line, if the system volume is Normal or
  Bad.
- **S4 is not a card.** It is S1's first line, added when H1 fired at ActNow or CheckAgain on a
  `system` or `unknown` disk. S1's rank (60) already keeps it below all hardware advice.

### 3.5 Not measured, notes, other findings, empty state

**Not measured.** One line per area, and only these three. Silence there would mislead about data
or hardware. Elsewhere the existing banner already says what did not finish
(`HtmlReportExporter.cs:77-93`).

| Area | When | Text key |
|---|---|---|
| drive-health | `disk.smart` or `disk.reliability` is Failed or PartiallyCompleted, unless H1 made an ActNow card on every disk that is not `external` | `advice.not-measured.drive-health`: "Drive health was not fully checked: {modules} did not finish, so a failing drive may not show here." |
| space | `volumes` is Failed or Partial, and there is no graded free_pct for the system volume | `advice.not-measured.space`: "Free space on {drive} was not measured: {modules} did not finish." |
| heat | `sensors` is Failed or Partial and no `cpu.temperature_c`/`cpu.zone_temperature_c` is graded; or `bench.cpu` is Failed or Partial, no clock reading is graded, and a CPU temperature is Normal or Bad | `advice.not-measured.heat` / `.heat-clock` |

- Module status alone does not decide: `volumes` going Partial over TRIM while C: was read is not a
  "space not measured".
- A card on one drive does not stand in for another (review round, 2026-10-28). A Plan or
  CheckAgain card on disk 1 says nothing about disk 0, whose counters the broken check would have
  read, and a worn drive may be failing as well. Only "back up now" on every drive inside the PC
  leaves nothing for the line to add.

**Notes** (grey, deliberate non-checks, never degrading):
- `advice.note.drive-health-not-included`: "Drive health was not part of this scan: its checks
  need administrator rights." Shown when neither `disk.smart` nor `disk.reliability` is Completed,
  PartiallyCompleted or Failed. That is, both are absent, or one is Skipped and the other absent.
  On an elevated scan (`AdviceContext.RanElevated`) the reason is not rights but the selection, so
  the same case shows `advice.note.drive-health-not-selected`: "Drive health was not part of this
  scan: its checks were not selected." That is a window started with Run as administrator and
  both drive checks unticked, or a headless scan from an elevated console without
  `--include-admin`.
- `advice.note.drive-health-not-reported`: "A drive in this PC did not report whether it has
  damaged areas or errors, so a failing drive may not show here." Shown when the drive checks ran,
  none broke, and a disk that is not `external` has no `DriveDamage` reading on its number (the
  disk benchmark's own test, `FailingDrives.HealthWasRead`), or no disk inside the PC has one at
  all. An NVMe laptop has `disk.smart` Skipped (`DiskSmartModule.cs:94-113`); with
  `disk.reliability` Completed and its read and write error counters in the report, it gets no
  note. An NVMe drive whose record is empty, or carries wear and a temperature only (pilot zip
  #1), gets it: nothing broke, but a failing drive would not show (review round, 2026-10-28).
- `advice.note.clock-not-included`: "Whether the processor slows itself down when hot was not
  measured: the processor speed test was not part of this scan." Shown when a CPU temperature is
  Normal or Bad and `bench.cpu` is absent or Skipped. The kit always skips it
  (`HeadlessScan.cs:78-85`).

**Other findings.** `OtherFindings` is the count of non-InfoOnly verdicts graded Normal or Bad, plus
trends graded Normal or Bad, that no recommendation uses as evidence.
- Rendered as "{count} other readings are graded Normal or Action needed; their tips are in their
  sections below", with one/other plural.
- Zero means no line. Other findings never become cards: see the rejected "fallback cards" in
  section 12.

**Empty state.**
- No recommendations: "Nothing to suggest from the checks that ran."
- If any module is Failed or Partial, this follows: "{count} checks did not finish (listed above),
  so this is not an all-clear."
- Then the other-findings line, if any.
- The empty state never contains "Some checks could not finish", "not the same as being healthy"
  or "INCOMPLETE". `ReportExportTests.cs:172-179` checks clean reports for exactly those.

### 3.6 Cap and "Don't show again" (presentation, not engine)

- `AdvicePresentation.Arrange(AdviceSection, IReadOnlySet<string> hiddenFingerprints, int cap = 5)`
  returns `(Shown, More, Hidden)`. It is pure and lives in Core, so it can be unit tested; the test
  project has no Avalonia tests.
- The HTML and headless surfaces pass an empty hidden set. The GUI passes the store's set. Hidden
  cards do not count toward the 5.
- Every cap says what it dropped (HANDOFF.md:141): "N more", "N hidden". The hidden line adds "one
  of them marked Act now" when one is.
- Not-measured lines, notes and the other-findings line never count toward the cap.

### 3.7 Failure handling

- Each item runs in a try/catch. A throw, or an invariant violation (a card with no problem
  evidence, a line whose placeholder has no argument), becomes an `ItemErrors` line: "Advice about
  {area} could not be worked out ({error type}). The readings below are complete."
- The scan never fails because of advice. Grades are untouched, and module statuses and the banner
  are unchanged.
- Every test fixture asserts that `ItemErrors` is empty.

---

## 4. Catalog

- **The file.** `src/PcHealth.Core/Text/en.json`, embedded with
  `<EmbeddedResource Include="Text\*.json" WithCulture="false" />`, next to
  `PcHealth.Core.csproj:10`.
  - The plain name `en.json` has no culture segment. `advice.en.json` would be assigned culture "en"
    and moved to a satellite assembly. `WithCulture="false"` is what the plan asks for
    (`REMEDIES-PLAN.md:208-209`).
  - `RulesLoader` reads only names containing ".Rules." (`Rules.cs:102`), so the catalog never
    reaches it.
- **Content.** A flat object of key to entry. An entry is a string, or `{ "one": "…", "other": "…" }`,
  where `one` is used exactly when the arg named `count` equals 1.
- **Keys** are dotted, lower-case and fixed:
  - `advice.section.{title,intro,empty,incomplete,other-findings,more,hidden,hidden-act-now,show-hidden,dont-show,item-error}`;
  - `advice.urgency.{act-now,check-again,plan,advice}`;
  - `advice.area.<area>`, `advice.not-measured.*`, `advice.note.*`;
  - per item: `advice.h1.*`, `advice.o4.*` (with `advice.o4.setting.<metricId>`), `advice.o1.*`
    (with `advice.o1.blocker.<code>`), `advice.space.*` (with `advice.space.d4` … `.d9`),
    `advice.o3.*`, `advice.s1.*`, `advice.s4.*`;
  - reserved for Step C: `advice.m5.*`, `advice.m6.*`, `advice.s4.hard-disk`, `advice.s4.low-memory`;
  - `list.sep` (", ") and `list.and` (" and ").
- **Placeholders.** Named `{name}` only, with no format strings in templates. Every line is one
  whole sentence, so a translation can reorder it.
- **Formatting is done by unit, in code,** always through `string.Create(CultureInfo.InvariantCulture, …)`
  or `ToString(fmt, CultureInfo.InvariantCulture)`. Never `$"{x:0.0}"`, which CA1305 does not flag
  and which is REP-002's root cause (`warning_bug_and_solutions.md:699-702`).

| Unit | Format |
|---|---|
| Count | "0" |
| Percent | "0" + "%" |
| Gb | "0" at 10 or more, "0.0" below, + " GB" |
| Minutes | rounded up to 5, "0" |
| Days | "0" |
| Celsius | "0" + " °C" |
| Date | the arg's Text, already "yyyy-MM-dd" (InvariantCulture), as `SysOverviewModule.cs:176` |
| None/Text | verbatim |

- **A missing argument** makes `Render` return null. The item must then pick the key's `.unmeasured`
  variant, or drop the line. A null line in a card is an invariant violation (3.7), so "0 GB" or a
  raw "{usedGb}" can never be printed.
- **Content rules** (tested):
  - no `http://` or `https://` (`ReportExportTests.cs:110-111`);
  - no banner phrases;
  - no path from the `OneVersionOnly` list (`FreshInstallTests.cs:156-180`), such as "Startup apps"
    or "Settings > Update & Security";
  - Windows pages are written as "Win+R, then ms-settings:…" (version-neutral, as `os.json:72`);
  - the section's own interface strings are in the catalog too.
- **Step E** adds `Text/vi.json` with the same keys, and `AdviceCatalog.For(CultureInfo)` falls back
  per key to English.
  - The GUI and HTML already render from Key + Args in Step B; JSON `Text` stays English.
  - Step E's tests check key, placeholder and plural-shape parity.
  - Module names in not-measured lines stay English until Step E translates the module labels.

---

## 5. Items

All triggers read graded verdicts unless marked *(value)*. "Normal/Bad" means the verdict's grade
is Normal or Bad, which an InfoOnly row can never have. Item `needs` lists are used only for
section 3.5.

### H1 — this drive is failing or worn (area drive-health, needs `disk.smart`, `disk.reliability`)

**Trigger.** Per disk group (3.3), skipped when the role is `external`. First match wins:
1. **ActNow (rank 10)**, if any of these holds for the disk:
   - any `DriveDamage` verdict is Bad: `smart.predict_failure` (`smart.json:20-25`),
     `smart.attributes_at_threshold` (`:4-9`), `smart.reallocated` at 5 or more (`:36-42`),
     `smart.reported_uncorrectable` (`:56-61`), or `storage.read_errors_uncorrected` /
     `storage.write_errors_uncorrected` (`:232-251`);
   - `smart.pending_sectors` (`:88-94`) or `smart.offline_uncorrectable` (`:107-113`) is Normal or
     Bad;
   - a `CounterGrowth` trend (always Bad, `TrendAnalyzer.cs:174-176`) on a DriveDamage id of this
     disk.
2. **CheckAgain (rank 12):** `smart.reallocated` is Normal (1-4), with no growth trend.
3. **Plan (rank 20):** `storage.wear_pct` or `smart.ssd_percent_consumed` is Normal or Bad, meaning
   70% or more (`smart.json:186-212`).

**Never triggers:** `smart.crc_errors`, `smart.command_timeout` (including their growth trends,
which `TrendAnalyzer.cs:50-51` watches), temperatures, power-on hours, load cycles,
`events.disk_errors_90d`, `storage.volume.healthy`, `storage.volume_clean`.

**Urgency mapping.** It follows the app's own grade and the plan's urgency list
(`REMEDIES-PLAN.md:679-682`):
- Bad, pending sectors and uncorrectable sectors mean act now;
- a small count (graded Normal) with no history means check again.

**Text** (keys `advice.h1.*`; example: pilot T470p, 5 retired sectors, C:):
- **Title:**
  - ActNow on a `system` disk: "Back up now: the drive Windows runs from is failing";
  - ActNow on an `internal` disk: "Back up now: a drive in this PC is failing";
  - ActNow with role `unknown`: "Back up now: a drive is failing";
  - CheckAgain: "Back up this drive, then scan again in 1-2 weeks";
  - Plan: "Plan to replace this drive: it is worn".
- **Subject:** the damage verdict's Subject, model and size (`DiskSmartModule.cs:143-144`).
- **Finding** (role line, one of):
  - `system`: "This is the drive Windows runs from ({drive})."
  - `internal` with letters: "It holds {letters}."
  - `unknown`: "This scan could not tell whether this drive is inside the PC or plugged in over USB.
    If it is a USB drive, this advice is about that drive, not the PC."
- **Finding** (evidence), one line per reading, with one/other plurals:
  - reallocated: "It has retired {count} damaged areas so far."
  - pending: "{count} areas are unstable, and the data in them may already be unreadable."
  - offline: "Its own background check found {count} areas it could not recover."
  - reported: "It has reported {count} errors it could not correct."
  - predict: "Its own firmware predicts that it will fail."
  - threshold: "{count} of its health readings have reached the failure limit its maker set."
  - read: "It failed to read data back {count} times."
  - write: "It failed to write data {count} times."
  - wear: "It has used {pct} of its rated write endurance."
- **Finding** (growth, per trend): "The count has grown since the scan of {since}: {from} then,
  {to} now."
- **Finding** (Plan tier with a Projection trend on that metric): "It was at {from} on {since}; it
  is at {to} now." The trend's Message is never quoted (invariant 5).
- **Why:**
  - ActNow: "Drives that start doing this fail far more often, some without further warning."
  - CheckAgain: "A small count that stays the same is usually fine; a rising one is not."
- **Do:** "First, copy your files off to an external drive or OneDrive, then open a few of the
  copies to check they work."
- **Do** (estimate, only with `used_gb`): "About {usedGb} is in use on {letters}, Windows and
  programs included. Copying all of it to a USB 3 drive takes about {minLow} to {minHigh} minutes at
  a typical 50 to 100 MB/s; your own files are usually much less."
  - `.data`, on every drive but the `system` disk, the only one known to hold Windows: "About
    {usedGb} is in use on {letters}. Copying all of it to a USB 3 drive takes about {minLow} to
    {minHigh} minutes at a typical 50 to 100 MB/s."
  - `.short`, when both ends round to the same 5 minutes (under about 14.6 GB in use, so only a
    data drive): "About {usedGb} is in use on {letters}. Copying all of it to a USB 3 drive takes
    {min} minutes or less at a typical 50 to 100 MB/s." Never "about 5 to 5 minutes".
- **Finding** (protection, for the account this scan ran as):
  - `backup.onedrive_folders` true and `backup.file_history` false: "OneDrive backs up some of
    this account's folders (the OneDrive folder backup row says which); anything else on this drive
    is not backed up."
  - `backup.onedrive_folders` true and File History on or not read (`.folders`): the same sentence
    without "anything else … is not backed up", which File History would make false.
  - `backup.file_history` true: "File History is on for this account, copying its files to another
    drive."
  - both false and `backup.any_protection` Bad: "Nothing this scan found is backing up this
    account's files."
  - both false and `any_protection` true (shadow copies only, `BackupProtection.cs:71-82`): "Only
    Windows' shadow copies were found, and they are no protection against a drive failing." The scan
    counts shadow copies without knowing which drive holds them, so the sentence never says "this
    same drive": on a failing D:, System Restore's copies are of C:, on C:.
  - the `file_history` row is missing: "Whether this account's files are backed up was not
    established in this scan."
- **Finding:** "If nothing is done, everything on this drive may be lost."
- **DoNot:** "Until your files are copied, do not run defrag, a full disk check (chkdsk /r), a speed
  test or 'repair' tools on it: they work the drive hard."
- **CheckAgain only:** "Then scan again in one to two weeks with 'Remember results to spot trends'
  on (`MainWindow.axaml:118`), so the app can tell whether the count is rising. If it has grown,
  replace the drive."
  - `.shared`, when another drive in the scan is the same model (3.3): "Write down the numbers
    above, then scan again in one to two weeks and compare them yourself: another drive this scan
    read is the same model, and the app's history cannot tell the two drives' readings apart. If a
    count has grown, replace the drive."
- **ForIt** (ActNow and Plan):
  - "Replace it with a known-brand SSD of at least {sizeGb} (1.5 times the space in use, rounded up
    to a standard size), with the same connection and physical size as the old one: check the
    laptop's manual."
  - `.unmeasured`: "…at least 1.5 times the size of the space in use…", with no GB.
- **ForIt:** "When the drive is retired: if BitLocker was on, delete its recovery key and wipe the
  drive; if not, destroy it or use its maker's secure erase."
- **Finding** (ActNow/CheckAgain, the system volume Normal or Bad):
  - `system`: "{drive} is also low on space ({freePct} free). Freeing space does not fix a failing
    drive: copy your files off first."
  - `unknown`: "If this is the drive Windows runs from: {drive} is also low on space. Free space
    only after your files are copied."

**Numbers.**
- `count` is the triggering verdict's `Value.Number`.
- `from`, `to` and `since` are `TrendFinding.PreviousValue`, `CurrentValue` and `PreviousSeen`
  (yyyy-MM-dd).
- `usedGb` comes from `storage.disk.used_gb`, and `letters` and `drive` from the role row's Subject
  and the context.
- `minLow` = ceil5(usedGb × 1024 / 100 / 60) and `minHigh` = ceil5(usedGb × 1024 / 50 / 60). They are
  labelled "about" and "typical" (principle 13).
- `sizeGb` is the smallest of 128, 256, 512, 1000, 2000 or 4000 that is at least usedGb × 1.5.
  Those are the sizes on the box, in 10^9 bytes, while `used_gb` is in 1024^3 bytes, so usedGb is
  multiplied by 1.073741824 first. Without that, 165 GB in use picked a "256 GB" SSD, which holds
  238.4 GB as Windows counts them: 1.44 times, not 1.5 (review round, 2026-10-28).
- No used_gb row, no role row, or a group keyed by Subject: those lines take their `.unmeasured`
  form or are dropped. The card never says "holds C:" or "0 GB" without the data.

**Silent on a healthy PC.**
- Every damage counter reads 0 and predict-failure is false, so all grade Good. With no growth
  trends and wear under 70%, no tier can match, and invariant 1 forbids it in any case.
- The auditor's USB drive has role `external`, so its Bad read errors stay in the table and in the
  kit note, and never produce a card.
- A reimage does not reset drive counters: a reimaged laptop with a healthy SSD is silent.
- Failed drive modules give "Drive health was not fully checked" (3.5).

### O4 — security settings are switched off (area security, rank 40, needs `security.posture`)

**Trigger** (anyOf, one card):
- `security.firewall.enabled` Bad, per profile (`security.json:80-89`; `SecurityPostureModule.cs:190-205`);
- `security.defender.rtp` Bad (`security.json:29-35`). This row is InfoOnly when Defender is not the
  antivirus in charge (`SecurityPostureModule.cs:119-129`);
- `security.av.registered` Bad (`security.json:5-9`);
- `security.defender.tamper` Normal (`security.json:56-62`; emitted only when Defender is in charge,
  `SecurityPostureModule.cs:137-140`);
- `security.uac.enabled` Bad (`security.json:91-99`);
- `security.smartscreen` Bad, value "Off" (`security.json:113-119`).

Not triggers: the secure desktop, an unrecognized SmartScreen state, signature age, exclusions.

**Text.**
- **Title:** "Security settings are switched off".
- **Finding**, one per setting (`advice.o4.setting.<metricId>`, the profile from the Subject):
  - "Windows Firewall is off for the {profile}."
  - "Microsoft Defender real-time protection is off."
  - "No antivirus is active."
  - "Defender tamper protection is off."
  - "User Account Control is off."
  - "SmartScreen is off."
- **Do:**
  - "Open Windows Security from the Start menu and turn them back on: Firewall & network protection,
    and Virus & threat protection, then Manage settings."
  - For UAC: "Type UAC in Start and set the slider back to its default level."
  - For SmartScreen: "In Windows Security, open App & browser control."
- **Finding**, always shown: "On a company laptop your IT team may have set this; ask them first.
  If another security product is in charge, check that product's settings instead."
- **Why**, only when RTP or AV is off: "If nobody turned this off on purpose, treat it as a warning
  sign: malware often switches protection off first. Run a full scan."

**Numbers.** None. The profile name comes from the Subject.

**Silent.**
- Windows' defaults grade Good on every one, so a freshly reimaged laptop has them.
- With a third-party antivirus, RTP is InfoOnly.
- No reading shows whether a laptop is managed. Domain and policy reads are deferred to Step C, so
  the IT line is always shown and worded as a possibility.

### O1 — Windows 11 is blocked only by a firmware setting (area windows, rank 50, needs `sys.overview`)

**Trigger** (allOf):
- `os.win11_ready` Normal, i.e. false (`os.json:49-55`, Windows 10), *or*
  `os.win11_hardware_supported` Normal (`os.json:34-40`, Windows 11);
- *(value)* `os.win11_blockers` Text (InfoOnly, `SysOverviewModule.cs:195-198`) is non-empty and not
  "none", and every space-separated code is in {Tpm, TpmVersion, UefiSecureBoot, SecureBoot, Uefi}.
  That is the kit's firmware-only rule (`audit-common.ps1:797-799`).

Any processor code (Cpu*, PopCnt, Sse*), SystemDriveSize, Memory, DX12 or an unknown code keeps it
silent. Decision 9 (`REMEDIES-PLAN.md:974`) makes a processor block a note, and the row's own tips
explain it.

**Text.**
- **Title:** "For IT: Windows 11 is blocked only by a firmware setting".
- **Finding**, one per code (`advice.o1.blocker.<code>`, worded from `Win11Readiness.cs:52-56`):
  - "The TPM security chip is off or not ready: turn on Intel PTT or AMD fTPM (the TPM) in the
    BIOS/UEFI settings."
  - "Secure Boot is off or not available: turn it on in the BIOS/UEFI settings if the setting
    exists."
  - "This PC starts in legacy BIOS mode: Windows 11 needs UEFI."
- **Finding**, instead of the code's line when this scan reads the setting as on since Windows last
  checked (it checks about once a day, so IT may scan again straight after switching it on):
  - Tpm or TpmVersion, with `security.tpm_ready` true and `security.tpm_version` 2.x
    (`advice.o1.now-on.tpm`): "Windows' last check found no TPM 2.0 security chip turned on, but
    this scan reads a TPM 2.0 as on: Windows has not checked again since."
  - SecureBoot or UefiSecureBoot, with `security.secure_boot` true (`advice.o1.now-on.secureboot`):
    "Windows' last check found Secure Boot off or not available, but this scan reads it as on:
    Windows has not checked again since."
  - Uefi has no reading of its own and keeps its line. Without the platform check's readings, as on
    a standard user's scan, every code keeps its line.
- **ForIt:** "Before any TPM or BIOS change: if BitLocker is on, suspend it and keep the recovery
  key."
- **ForIt**, only with Uefi, UefiSecureBoot or SecureBoot: "On a Windows installed in legacy BIOS
  mode, switching to UEFI or Secure Boot stops it starting. Back up first, then convert the disk
  with mbr2gpt, or reimage."
- **Finding**, Windows 11 branch only: "Windows 11 runs here, but Microsoft supports it on this PC
  only once these are switched on."
- **Do:** "Windows checks again by itself about once a day: leave the PC on for a day, then scan
  again or run the kit again." This matches `SysOverviewModule.cs:170-172`.
- **Finding:** "Probably ready for Windows 11 after the change: nothing else blocks it."

**Numbers.** None. The assessment date is in the Detail only (`SysOverviewModule.cs:175-179`) and is
never parsed, so the text always says to check again.

**Silent.**
- A supported PC reads Good.
- All three pilots and the T470 fleet report CpuFms. One pilot reports "CpuFms Tpm", which is also
  silent.
- With no verdict, or a verdict that predates the install (a Note, `SysOverviewModule.cs:184-193`),
  there is no blocker row.
- A reimaged laptop gets O1 only when a switch really is its only blocker. That is a real prep item.

### SPACE — the system drive is low on space: the D4-D9 advice (area space, rank 45, needs `volumes`)

**Card trigger.** The system volume's `storage.volume.free_pct` is Normal (10-20%) or Bad (under
10%) (`storage.json:5-11`).
- USB volumes are InfoOnly (`VolumeReadings.cs:23-26`) and never match.
- The card is dropped when H1 fired on a `system` or `unknown` disk (3.4).

**Title:**
- Normal: "{drive} is getting full: {freePct} free".
- Bad: "{drive} is running out of space: {freePct} free. Under 10% free, Windows Update can fail."

**Lines, safest first:**
1. **Do** (D7): "Open Disk Cleanup (Win+R, type cleanmgr, choose {drive}), then press 'Clean up
   system files'. Windows shows how much each kind of file frees, old update files included."
2. **Do:** "Storage Sense can do part of this on a schedule (Win+R, then ms-settings:storagesense).
   If its switch is greyed out, IT controls it."
3. **Do** (D7, only when `events.bugcheck_90d` or `events.gpu_tdr_90d` is Normal or Bad,
   `stability.json:69-75, 85-91`): "This PC has had recent blue screens or graphics resets. Leave
   'System error memory dump files' and error reports unticked: they are the evidence for a
   repair."
4. **Do** (D7, only when `integrity.image_health` is "Repairable", Normal, `smart.json:270-276`):
   "Windows' image check says 'repairable': run that repair first (the Windows image row says
   how). A clean-up is not a repair."
5. **DoNot** (D7): "Leave restore points alone: do not use 'Clean up' under System Restore and
   Shadow Copies."
6. **DoNot** (D5): "Leave the page file, reserved storage, and the Windows Installer and Package
   Cache folders alone: Windows needs them to install updates, and programs need them to repair or
   uninstall themselves."
7. **DoNot** (D4, only when Bad): "The hibernation file is not junk: it lets open work survive a
   flat battery, and Fast Startup uses part of it. On a laptop, keep it. On a desktop, an
   administrator can run 'powercfg /h /type reduced' to free about half of it and keep Fast
   Startup."
8. **Finding** (D6): "If Disk Cleanup lists 'Previous Windows installation(s)': that copy lets you go
   back to your previous Windows. Remove it only if you will not need to go back; Windows removes it
   by itself after a few weeks. This app never deletes it."
9. **Do** (D8): "Downloads: sort it by size in File Explorer and delete what you no longer need.
   Deleting an installer does not uninstall the program. On a company laptop, do not switch on
   Storage Sense's automatic Downloads clean-up: it deletes files by age alone."
   - Only when `backup.onedrive_folders` is true: "OneDrive's 'Free up space' keeps a file online
     and downloads it again when you open it, which needs the internet."
10. **Finding** (D9, last): "Browser, Teams and Office caches refill within days, so clearing them
    frees space only for a while. If you do, use each app's own control: in a browser, 'Clear
    browsing data', then 'Cached images and files' only. Outlook .pst files are your mail, not a
    cache."
11. **Finding:** "Free space is graded Good above 20%. Freed space makes room for updates and files;
    it does not make the PC faster."

**Numbers.** Only `freePct`, from the reading, and `drive`, from the context. The hibernation file,
page file, reserved storage, old Windows copy, Downloads and cache sizes are all Step C rows 6 and 9.
Until then no line names a size, and none can print 0.

**Silent.**
- Above 20% free grades Good. The pilots and reimaged laptops have 52-82% free on C:.
- A full USB drive is InfoOnly.
- A failing system drive drops the card.
- Decision 3 holds: nothing is offered for deletion by the app (`REMEDIES-PLAN.md:965`).

### O3 — this PC runs hot and slows itself down (area heat, rank 25, needs `sensors`, `bench.cpu`)

**Trigger** (allOf):
- `cpu.temperature_c` or `cpu.zone_temperature_c` is Normal or Bad, meaning 80 °C and up
  (`sensors.json:5-11, 27-33`);
- `bench.cpu.clock_pct_of_base` is Normal or Bad, meaning under 100% (`benchmarks.json:5-11`). On
  battery that row is InfoOnly (`CpuBenchmarkModule.cs:83-90`).

`events.cpu_speed_limited_30d` is InfoOnly with no healthy baseline (`EventsModule.cs:58-66`). It
appears in the text and never triggers. The plan's "OR throttle events" branch waits for a band set
on healthy PCs (principle 14).

**Text.**
- **Title:** "This PC runs hot and slows itself down".
- **Finding:** "With one core busy, the processor ran at {clockPct} of its base speed. A healthy one
  goes above 100%. Earlier in this scan it read {tempC}." The sensors check runs before the
  benchmark (`HeadlessScan.cs:129-132`), so the temperature is not a reading under load.
- **Finding**, only when the count is above 0: "Windows also logged {count} times in the last 30
  days that the firmware held the processor back."
- **Why:** "Usually that is dust in the fan or dried-out heat paste. A charger weaker than the laptop
  needs does the same."
- **Do:**
  - "Keep the vents clear: not on a bed, a cushion or your lap."
  - "Use the charger that came with the laptop; its wattage is printed on it."
- **ForIt:**
  - "Clean the fan and heatsink, and replace the heat paste."
  - "Install the laptop maker's BIOS updates (this needs the internet). This app does not change
    power settings."
- **Laptop or not.** The lines above, with the charger, the lap and the laptop maker, need a
  `battery.*` reading in the report. A desktop has none (its battery check is Skipped), and the
  speed test grades a PC with no battery as on mains, so a hot desktop gets the card too. Without a
  battery reading it reads `.pc` (review round, 2026-10-28):
  - **Why:** "Usually that is dust in the fan or heatsink, or dried-out heat paste."
  - **Do:** "Keep the PC's air vents clear." No charger line.
  - **ForIt:** "Clean the fan and heatsink, and replace the heat paste." and "Install the BIOS
    updates from the maker of the PC or its motherboard (this needs the internet). This app does
    not change power settings."
  - A laptop whose battery was not read gets these too, which loses only the charger line.
- **Finding:** "The next scan's processor speed shows whether it helped."

**Numbers.**
- `clockPct` comes from the clock reading.
- `tempC` is the highest graded CPU temperature.
- `count` comes from `events.cpu_speed_limited_30d`. Its line is dropped when the reading is missing
  or 0.

**Silent.**
- A cool CPU, or one that still turbos, grades Good on one of the two.
- A reading taken on battery is InfoOnly.
- The kit never runs the benchmark, so at most the grey note appears (3.5).

### S1 — restart Windows once, with S4's label (area startup, rank 60, needs `sys.overview`)

**Trigger** (allOf):
- *(value)* `os.uptime_days` is greater than 7. It comes from LastBootUpTime
  (`SysOverviewModule.cs:40-45`), which Fast Startup does not reset. The uptime row itself grades
  Good up to 30 days (`os.json:83-92`), so this test alone can never fire.
- One of:
  - `os.pending_reboot` Normal, i.e. true (`os.json:94-99`; `SysOverviewModule.cs:102-103`);
  - `memory.commit_pct` Normal or Bad, meaning 80% and up (`memory.json:5-11`).

`memory.low_memory_events_30d` is not a trigger. A 30-day count says nothing about the time since
the last restart, and that question is M2's (Step C row 7).

**Text.**
- **Finding** (S4), first line, only under 3.4's condition: "This will not fix a slow start: the
  drive Windows runs from is failing. Back it up first (see above)."
- **Title:** "Restart Windows once (Restart, not Shut down)".
- **Finding:** "Windows has not fully restarted for {days} days."
- **Finding**, if a restart is pending: "A restart is waiting: updates or repairs finish only after
  it."
- **Finding**, if commit is high: "Programs have been promised {commitPct} of what Windows can hand
  out; a restart clears what they built up."
- **Why:** "If you usually switch off with Shut down: with Fast Startup on (Windows' default), Shut
  down saves Windows' core to disk so the next start is faster, and that does not count as a
  restart."
- **Do:** "Save your work, then choose Start, Power, Restart. About once a week is enough. Keep Fast
  Startup on: it is not a fault."
- There is no "Restart now" button (`REMEDIES-PLAN.md` section 7).

**Numbers.**
- `days` comes from uptime, `commitPct` from commit.
- The plan's "3 waiting updates" is not used: `updates.pending_count` counts updates not yet
  installed.
- The Fast Startup state and the number of fast starts are Step C row 2, so the sentence stays
  conditional.

**Silent.**
- Restarted within 7 days, or no pending restart and commit under 80%: silent.
- A freshly reimaged laptop has been up for minutes or hours.
- On a healthy SSD, S1 appears **without** the S4 line.

### S4 — the real cause is hardware (a rule in the engine, not a card)

- Live in Step B: the failing-system-drive label above.
- The hard-disk variant ("This laptop runs Windows from a hard disk…") and the low-memory variant
  are catalog text only. Their readings are Step C row 3 (system drive type, H4) and rows 1 and 7
  (M3, M2, for M6).
- Silent unless H1 fired at ActNow or CheckAgain on a `system`/`unknown` disk.

### M5, M6 — catalog text only

- The keys `advice.m5.*` and `advice.m6.*` carry the plan's wording (`REMEDIES-PLAN.md:555-571`), so
  Step E translates them with the rest.
- No trigger code ships. M6 needs "no leak (M5) explains it", which cannot be established until
  Step C row 1 reads the low-memory culprits. An unread leak must not read as no leak (rule 1).
- The kit's existing "Add memory" fix for under 8 GB is unchanged (`audit-common.ps1:707-710`).

---

## 6. Surfaces

**JSON.** The `Advice` object (2.2): complete, uncapped, English, never filtered by dismissals.

**HTML** (`HtmlReportExporter.cs`):
- An `<h2>What you can do</h2>` after the incomplete banner (`:77-93`) and before the trends (`:95`).
  It is omitted when `Advice` is null.
- The intro: "Built only from the readings in this report. Hardware and your data come first. Each
  section below still has its own tips."
- The first 5 cards, each showing:
  - an urgency chip, using the existing `.chip` classes `g-<Severity>` (`:38-42`);
  - the title and subject;
  - a `<ul class="tips">`, with ForIt lines prefixed "For IT:".
- The rest inside `<details><summary>N more</summary>…</details>`. This is native HTML, so the
  report stays script-free (`:9-11`; `ReportExportTests.cs:98, 112`).
- Then the not-measured lines, the notes, the item errors, the other-findings line, or the empty
  state.
- Every string goes through the existing `E()` (`:224-240`), never WebUtility: System.Net is gated
  (`build-release.ps1:63-75`).
- The HTML always lists every recommendation, so a report sent to IT is never hidden by one user's
  choice.

**Headless console** (`HeadlessScan.cs`):
- The block goes after the category lines (`:218-219`) and the blank line (`:220`), before the
  per-module lines (`:221-222`):
  - "What you can do (N):";
  - up to 5 lines "  1. [Act now] <title> (<subject>)";
  - "  … and N more in the report.";
  - "  Not measured: <text>";
  - "  Note: <text>";
  - "  N other readings are graded Normal or Action needed; see the report.";
  - or "What you can do: nothing to suggest from the checks that ran."
- English. No dismissals.
- "Overall:" stays the first summary line (`:217`).
- Placing it before the module list keeps `collect-results.ps1:145` (`-Tail 12`) showing the same
  lines. The kit-tests regex checks on HeadlessScan.cs (`kit-tests.ps1:409-413`) are untouched.
- Exit codes are unchanged (`:22-35`).

**GUI** (`src/PcHealth.App`):
- A new partial, `ViewModels/MainWindowViewModel.Advice.cs`, on the pattern of `.Reputation.cs`.
  It holds:
  - `AdviceCards` and `MoreAdviceCards` (ObservableCollection of `AdviceCardViewModel`);
  - `HasAdvice`, `AdviceEmptyText`, `MoreAdviceHeader`, `NotMeasuredText`, `NotesText`,
    `OtherFindingsText` and `HiddenAdviceText`;
  - `[RelayCommand] DismissAdvice(card)` and `[RelayCommand] ShowHiddenAdvice()`.
- `PresentReport` (`MainWindowViewModel.cs:304-377`) calls `PresentAdvice(report.Advice)`. The
  collections are cleared where the scan clears the others.
- A card shows:
  - an urgency chip, coloured by `GradeVisuals.Brush(Severity)` (`MainWindowViewModel.cs:483-489`);
  - the title and subject;
  - the lines, with ForIt lines labelled "For IT", so an employee knows to pass them on;
  - a "Don't show again" link button.
- `MainWindow.axaml`: a new Border "What you can do" goes after the incomplete banner (`:173-180`)
  and before "What changed since your last scan" (`:182-218`). It contains the cards, an Expander
  "N more", the amber not-measured text, the grey notes, "N hidden — Show again", and the
  other-findings or empty state.
- Compiled bindings: every new DataTemplate names its `x:DataType` (`MainWindow.axaml:5, 190`).

**The "Don't show again" store** (`src/PcHealth.Core/Advice/DismissedAdviceStore.cs`):
- It lives at `%APPDATA%\PcHealthCheck\dismissed-advice.json`, beside consent.json
  (`ConsentStore.cs:34-37`).
- Contents: `{ "<first 16 hex of Fingerprint>": "2026-10-28" }`. Only hashes and dates: no drive
  names, numbers or letters.
- A corrupt or unreadable file hides nothing. It fails open, the same default-state direction as
  `ConsentStore.cs:48-52`.
- A failed save says so in the status line.
- A card comes back by itself when it gets worse: higher urgency, Normal turning Bad, a new reading,
  a growth trend, or for H1 a higher count.
- "Show again" deletes the file.
- Only the unelevated GUI reads or writes it. The headless scan, the elevated helper and the kit
  never touch it, so the kit still leaves nothing on the laptop.

**PRIVACY.md**, same commits:
- the disk rows (work group 2);
- the dismissal file line (work group 4);
- "The 'What you can do' section is built only from readings already in this report."

---

## 7. Tests (both directions throughout)

**Catalog and format** (`AdviceCatalogTests`):
- Under CurrentCulture and CurrentUICulture vi-VN and de-DE, every rendered fixture is
  byte-identical to InvariantCulture: 8.3 → "8.3", 104.4 GB → "104 GB" (the REP-002 class).
- Plurals: 1 gives "1 damaged area", 5 gives "5 damaged areas".
- A missing argument gives null, never "{x}" or "0". No rendered text contains "{".
- Every key an item uses exists, and every catalog key is used or listed as reserved for Step C.
  Every placeholder is supplied on every path the fixtures reach.
- No entry contains "http", the banner phrases or any `OneVersionOnly` path
  (`FreshInstallTests.cs:156-180` is extended to scan en.json).
- A source scan fails on any `$"…{x:fmt}…"` under `src/PcHealth.Core/Advice` and `Text`.

**Invariants** (`AdviceEngineTests`):
- A test-only item that emits a card whose only evidence is InfoOnly, Good, or a Good projection
  trend is rejected: one ItemErrors line, the report still builds.
- An item that throws gives an ItemErrors line, and grades and module statuses are unchanged.
- A disk group keyed by DiskNumber vs by Subject (null DiskNumber); a trend joined by
  (MetricId, Subject); two identical models on different disks, only one failing, give one card on
  the right DiskNumber, and a trend on the series they share joins neither.

**Healthy and freshly reimaged, the gate** (`HealthyPcAdviceTests`):
- **Generated all-Good.** For every embedded Windows rule's first Good band, synthesize a value
  (eq/anyOf take the value; lte/gte the bound; lt/gt the bound ∓1). Add InfoOnly facts:
  `ram.total_gb` 15.9, `os.win11_blockers` "none", role `system` on disk 0, `used_gb` 100,
  `events.cpu_speed_limited_30d` 3, `backup.onedrive_folders` false. Add a Good Projection trend.
  With every module Completed, expect zero recommendations, zero not-measured lines, zero notes,
  `OtherFindings` 0, no item errors. The uptime Good bound is 30 > 7, which proves the value half of
  S1 cannot fire alone.
- **The same set, unelevated** (the admin modules absent): zero recommendations, zero not-measured
  lines, exactly the one drive-health note, and the module statuses (and so the banner) unchanged.
- **Fresh-reimage fixture, Windows 10 22H2 without ESU:**
  - uptime 0.2 with a restart pending;
  - `updates.pending_count` Bad, `os.supported` Bad, `security.defender.sig_age_days` Bad;
  - encryption Normal, `backup.any_protection` Bad, blockers "CpuFms", C: 70% free, a USB volume at
    5% (InfoOnly), the kit stick's `storage.read_errors_uncorrected` Bad on a disk with role
    `external`.

  Expect **zero recommendations**, OtherFindings > 0, and no item errors.
- **The same fixture with blockers "Tpm"** gives exactly O1. With uptime 9 it gives exactly S1.

**Per item, both sides of each threshold** (`AdviceItemTests`):
- **H1:**
  - reallocated 0 is silent, 3 gives CheckAgain, 5 gives ActNow;
  - pending 1 gives ActNow;
  - a growth trend from 2 to 3 lifts CheckAgain to ActNow;
  - a CRC-errors growth trend alone is silent;
  - wear 69 is silent, 75 gives Plan;
  - `crc_errors` 500, `command_timeout` 200, a temperature of 85 or `events.disk_errors_90d` 47
    alone are each silent;
  - role `external` is silent;
  - no role row gives the role-unknown line;
  - no `used_gb` gives no GB line, no "0 GB" and the `.unmeasured` replacement line;
  - each protection variant (OneDrive, File History, nothing, shadow copies only, not established);
  - on the system disk with C: at 8%, the SPACE card is dropped and the also-low line added; on an
    internal data disk, the SPACE card is kept.
- **O4:**
  - Public firewall off gives one card, one line;
  - three settings give one card, three lines;
  - RTP off while InfoOnly (another antivirus) is silent;
  - SmartScreen "Warn" is silent; "Off" fires.
- **O1:**
  - "Tpm" fires; "CpuFms Tpm", "SystemDriveSize", "none", "" and an unknown code are silent;
  - "Tpm Uefi" adds the mbr2gpt line;
  - the Windows 11 branch works through `os.win11_hardware_supported` Normal.
- **SPACE:**
  - C: at 21% is silent; 15% fires without the D4 line; 8% fires with it;
  - a "C: (label)" subject still matches, and the label never appears in the text;
  - a USB volume at 5% is silent;
  - bugchecks Normal add the dump line; "Repairable" adds the repair-first line; OneDrive true adds
    the OneDrive line;
  - no line names a size.
- **O3:**
  - 91 °C with the clock Good is silent; clock 78% at 70 °C is silent; both together fire;
  - a clock reading taken on battery (InfoOnly) is silent;
  - a firmware-limit count of 500 alone is silent;
  - a hot CPU with `bench.cpu` absent gives only the clock note.
- **S1/S4:**
  - uptime 7.0 with a restart pending is silent; 7.1 with a restart pending fires;
  - 25 days with commit 79% and no restart pending is silent; commit 86% fires;
  - with H1 ActNow on the system disk, S1's first line is the S4 label; on a healthy SSD, S1 has no
    label.
- **Not measured:**
  - `disk.smart` Failed, and separately PartiallyCompleted, give the drive-health line;
  - `disk.smart` Skipped with `disk.reliability` Completed (NVMe) and the drive's read and write
    error counters read gives nothing; the same with wear only, or with no reading of the drive,
    gives the grey not-reported note, and an unread USB drive gives none;
  - H1 ActNow on every drive inside the PC: no drive-health line; a Plan or CheckAgain card, or a
    card on another drive only, still gets it;
  - both drive checks absent: the administrator-rights note on a scan without them, the
    not-selected note on an elevated one;
  - `volumes` Partial with C: read gives no space line; Failed with no C: reading gives the line;
  - `sensors` Failed with no temperature gives the heat line; Partial with a temperature read gives
    none;
  - not-measured lines never count toward the cap.
- **Order and cap** (`AdvicePresentationTests`):
  - H1 ActNow, H1 Plan, O3, O4, SPACE, O1, S1 come out in that order; Bad before Normal within a
    rank;
  - the order is identical over 100 shuffled inputs;
  - 7 cards give 5 shown and "2 more";
  - dismissed cards are taken out, counted as hidden, and do not count toward the 5; the
    hidden-act-now variant appears when one of them is ActNow.
- **Source check:** every metric id an item reads appears as a string literal in
  `src/PcHealth.Modules.Windows/**/*.cs`. It catches typos, the way kit-tests reads
  `WindowsSupport.cs` (`kit-tests.ps1:767`).
- **Evidence:** no card line contains any metric's Detail text. The test uses an exclusions row
  whose Detail holds a path.

**Disk rows** (`UsbAndDriveKindTests`, D1 style):
- a USB disk gives `external`;
- the boot disk on USB gives `system`;
- unknown, NVMe and RAID buses give `internal`;
- a disk with no letters gets Subject "Disk N";
- `used_gb` is the sum of the lettered volumes, and is not emitted when one of them is unmeasured;
- an unreadable map gives no rows and keeps the existing note with the status unchanged;
- a Subject never contains a volume label.

**DriveDamage:** `FailingDrives` behaviour is unchanged (the existing benchmark tests). H1's damage
set *is* `DriveDamage.MetricIds`.

**Report** (`ReportExportTests`, `ScanOrchestratorTests`):
- BuildReport sets `Advice`. The JSON has SchemaVersion "1.1" and every Advice field, with
  `Args.count.Number` readable.
- Verdicts, CategoryGrades and OverallGrade are identical with and without an advice engine.
- With 7 cards the HTML shows 5 cards and a `<details>` "2 more".
- A clean report shows the empty state and still contains no "INCOMPLETE", no "Some checks could
  not finish", no `<script` and no http(s).
- A hand-built report (`Advice` null) has no section.
- An OS of "linux" gives `Advice` null.

**Dismiss store** (`DismissedAdviceTests`):
- a save and load round trip in a temp folder;
- a corrupt file hides nothing;
- the file holds no model string and no letter;
- the fingerprint is stable over a repeated scan, and changes on Normal → Bad, on higher urgency,
  on a new growth trend, on an H1 count rising, and on a new piece of evidence.

**Kit** (`kit-tests.ps1`, ASCII):
- One new check in both directions: a bundle whose report has an `Advice` object and role/used_gb
  rows (including a role row on the stick's disk) gives a row identical to the same bundle without
  them. Compared: Verdict, Reasons, Notes, DriveHealth, DriveRead, SystemDriveFree and Windows11.
- The existing 285 checks stay green.

**Pilot replay** (`PilotReplayTests`):
- It uses a `FactAttribute` that sets `Skip` when `audit-results/` has no matching zips, the
  `LiveLinuxTests.cs:15-19` pattern (xUnit v2 has no `Assert.SkipWhen`, HANDOFF.md:398).
- **Selection.** Top-level `audit-results/*.zip` only. Zips are opened in memory, with entry names
  matched with either slash. The three pilots are the zips whose report `AppVersion` contains
  "+af47a36". That build made only those three (NEXT-STEPS), and later fleet zips are ignored. The
  test asserts exactly three, tells them apart by content, and never prints a file name, Subject or
  MachineName.
- **Harness.**
  - Re-grade `Verdicts[].Metric` with the current VerdictEngine.
  - Pilot-era JSON has no DiskNumber and no role rows (read: none of the three has the field). For
    each distinct Subject on drive readings, the harness assigns a synthetic DiskNumber (100+i) and
    a role row:
    - `external` when `disks.json` lists that FriendlyName with BusType USB/1394/SD/MMC (matched as
      `audit-common.ps1:197-205` does);
    - `system` when it matches `machine.json` SystemDiskModel;
    - otherwise `internal`.
  - SystemDrive comes from `machine.json`. Trends are empty: the kit runs with `--no-history`.
  - This transform is harness-only; the engine is unchanged.
- **Expected, verified by reading the three reports:**
  - the elevated pilot T470p (internal SATA SSD, `smart.reallocated` 5): exactly one H1 ActNow,
    with the system-drive title;
  - the elevated T470s whose `disk.smart` Failed: zero cards, one drive-health not-measured line;
  - the unelevated T470s (every admin module Failed): zero cards, drive-health and heat
    not-measured lines;
  - none of them: any card about the auditor's USB drive (Bad read errors on two of the three),
    any SPACE card (the only Normal free space is a USB volume), O1 (CpuFms on all three) or S1
    (uptime under a day);
  - no item errors.
- **For every zip in the folder** (later fleet zips included): no recommendation lacks problem
  evidence, and no H1 sits on a disk the harness tagged external.

**By hand:** section 9.

---

## 8. Work groups, in order

### 1. Advice core: model, catalog, formatter, engine shell, report wiring (no product items)

- **Files:**
  - `src/PcHealth.Core/Advice/{AdviceModel,AdviceFacts,AdviceEngine,AdviceThresholds}.cs`;
  - `src/PcHealth.Core/Text/{AdviceCatalog,AdviceFormat}.cs` and `src/PcHealth.Core/Text/en.json`
    (interface strings only);
  - `src/PcHealth.Core/PcHealth.Core.csproj`;
  - `src/PcHealth.Core/Reporting/ScanReport.cs`, `src/PcHealth.Core/Orchestration/ScanOrchestrator.cs`;
  - `tests/PcHealth.Tests/{AdviceCatalogTests,AdviceEngineTests}.cs`, plus additions to
    `ReportExportTests.cs` and `ScanOrchestratorTests.cs`.
- **Done when:**
  - `dotnet build PcHealth.slnx -c Release -warnaserror` is clean (CA1305/CA1304/CA1310 are
    errors);
  - the catalog and invariant tests pass, including vi-VN/de-DE identity and the source scan;
  - BuildReport sets an empty `Advice` and SchemaVersion "1.1";
  - grades are identical with and without the engine;
  - `RulesLoader.LoadAllEmbedded()` returns the same count;
  - Linux gives `Advice` null;
  - all existing tests, the 285 kit checks and `daily-smoke.ps1` are green.

### 2. Disk facts from VolumesModule, and DriveDamage to Core

- **Files:**
  - `src/PcHealth.Modules.Windows/Support/DiskBus.cs` (`DiskOf`, `DiskRoles`);
  - `src/PcHealth.Modules.Windows/Support/VolumeReadings.cs` (row builders);
  - `src/PcHealth.Modules.Windows/Modules/VolumesModule.cs`;
  - `src/PcHealth.Core/Metrics/DriveDamage.cs`;
  - `src/PcHealth.Modules.Windows/Support/FailingDrives.cs`;
  - `tests/PcHealth.Tests/UsbAndDriveKindTests.cs`;
  - `tests/audit-kit/kit-tests.ps1`;
  - `PRIVACY.md`.
- **Done when:**
  - the disk-row tests pass, and the FailingDrives and benchmark tests are unchanged and green;
  - kit-tests pass 285 + 1 (both directions), with the file still ASCII;
  - an unelevated live scan on the dev T470p shows one role row per disk, C:'s disk as `system` and
    each USB disk as `external`, with `used_gb` on each;
  - one person-run elevated scan (section 9) shows each SMART/reliability row's DiskNumber equal to
    the role row's DiskNumber for the same drive;
  - PRIVACY.md is in the same commit;
  - the daily smoke is green.

### 3. The Step B items and their English text

- **Files:**
  - `src/PcHealth.Core/Advice/Items/{DriveHealthItem,SecurityOffItem,Windows11SwitchItem,LowSpaceItem,HotAndSlowItem,RestartItem}.cs`;
  - `AdviceEngine.cs` (registration, not-measured, notes, other findings);
  - `Text/en.json` (item keys plus the reserved M5/M6/S4 keys);
  - `tests/PcHealth.Tests/{AdviceItemTests,HealthyPcAdviceTests}.cs`.
- **Done when:**
  - the generated all-Good test, the unelevated variant and the fresh-reimage fixture give zero
    recommendations;
  - the Tpm and uptime variants give exactly O1 and S1;
  - every per-item test passes both sides of its threshold;
  - the not-measured and note tests pass both ways;
  - the source-literal and catalog-coverage tests pass;
  - `ItemErrors` is empty on every fixture.

### 4. Surfaces: HTML, headless console, GUI, cap and "Don't show again"

- **Files:**
  - `src/PcHealth.Core/Advice/{AdvicePresentation,DismissedAdviceStore}.cs`;
  - `src/PcHealth.Core/Reporting/HtmlReportExporter.cs`;
  - `src/PcHealth.App/HeadlessScan.cs`;
  - `src/PcHealth.App/ViewModels/MainWindowViewModel.cs` and `MainWindowViewModel.Advice.cs`;
  - `src/PcHealth.App/Views/MainWindow.axaml`;
  - `PRIVACY.md`, `ACCEPTANCE.md` (new rows);
  - `tests/PcHealth.Tests/{AdvicePresentationTests,DismissedAdviceTests}.cs`, plus
    `ReportExportTests.cs` additions.
- **Done when:**
  - the presentation, store and HTML tests pass, and the existing banner, no-script and no-URL tests
    still pass;
  - `kit-tests.ps1:409-413` still pass;
  - the daily smoke is green: console UTF-8 with its em dash, the network line, no stray writes;
  - the new ACCEPTANCE rows have been run by a person (section 9).

### 5. Pilot replay, baselines, records

- **Files:**
  - `tests/PcHealth.Tests/PilotReplayTests.cs`;
  - `docs/REMEDIES-PLAN.md`: Step B built; S4's hard-disk label moved to Step C row 3; managed-laptop
    reads (domain and policy keys, for O4) added to Step C; the O3 throttle-event band added to the
    baseline list;
  - `HANDOFF.md`, `NEXT-STEPS.md`, `CLAUDE.md` (test counts), `README.md`;
  - `docs/warning_bug_and_solutions.md`.
- **Done when:**
  - the replay passes on the dev T470p with the three af47a36 zips, and is skipped (not failed) on a
    clone without them;
  - both dev PCs have been scanned, one elevated, with no unexpected card;
  - every fault found has a fault-log entry, wrong first guess included;
  - the work is committed first, and only then is the release built (`build-release.ps1`, stamped
    `+<HEAD>`), and the stick re-copied if shipping.

---

## 9. What a person has to run (the smoke question)

- **Nothing new until work group 2.** After every group, `daily-smoke.ps1` on the dev T470p is the
  check. It runs the tests, the kit checks and a real unelevated scan. On the dev desktop the smoke
  reports `Scan: SKIP` because DiskSpd is installed (HANDOFF.md:350-355), so a green smoke there is
  not a scan.
- **After work group 2, one elevated scan on the T470p** (it needs the UAC click): confirm that the
  drive rows' disk numbers match the new role rows.
- **After work group 4, new ACCEPTANCE rows, in the GUI on the T470p:**
  - the section appears after a scan, above "What changed";
  - "N more" expands;
  - "Don't show again" hides a card, the card stays hidden after the app restarts, and it returns
    after a scan with a worse state;
  - "Show again" restores it;
  - an exported HTML still lists the hidden card;
  - a standard (unelevated) scan shows the grey drive-health note and no amber text;
  - the Windows Security / ms-settings wording matches the PC's Windows.
- **Before Phase 2:** one headless `--skip-benchmarks` scan on the Windows 11 dev desktop
  (`REMEDIES-PLAN.md:886-887`). No Windows 11 run has happened yet.

---

## 10. Risks

1. **The replay is only as good as the disk join.** Pilot-era JSON has no DiskNumber. Without the
   harness tagging the USB drive as external, H1 would say "back up now" about the auditor's drive
   on two of three pilots. The tagging is harness-only and must never leak into the engine.
2. **A disk with no role row counts as internal.** This happens when Volumes was not selected or the
   map was unreadable. A USB drive's Bad read errors can then raise H1. That is the kit's safe
   direction (`audit-common.ps1:170-174`). The role-unknown line says so on the card.
3. **MSBuild culture assignment.** A name like `advice.en.json` or `vi.json` placed under a
   culture-looking segment vanishes into a satellite assembly. Use `Text/en.json` with
   `WithCulture="false"`, and test that the resource loads.
4. **REP-002 leaks in if anyone quotes a trend Message or a Detail** (`TrendAnalyzer.cs:258-264,
   294-296`; `SysOverviewModule.cs:62`). Invariant 5 and the source scan guard against it. REP-002
   itself stays open.
5. **An empty section read as an all-clear.** The section is deliberately narrow: updates,
   batteries, Defender age and encryption stay in their rows. The intro, the other-findings count,
   the not-measured lines and the incomplete empty-state variant carry this.
6. **With history off, a counter can grow while its grade stays the same** (reallocated 5 → 9, both
   Bad) and produce no CounterGrowth trend. The H1 fingerprint includes the counts, so a dismissed
   card still returns. History is on by default (`MainWindowViewModel.cs:42`); the kit runs without
   it.
7. **O4 inherits today's firewall grading.** A third-party firewall, or a Domain profile off on a
   workgroup PC, grades Bad, and O4 fires. The text hedges. The fix belongs to the reading, in
   Step C.
8. **S1 assumes Fast Startup may be on** until Step C row 2. The wording is conditional.
9. **The SPACE card is long and generic until Step C rows 6 and 9 give sizes.** It is still capped
   at one card.
10. **Every report gains two INFO rows per disk, and history gains a used-GB series per disk.** That
    is visible but harmless. Subjects are drive letters, never labels.
11. **Admin rights limit H1 and O3 for standard users** (`DiskSmartModule.cs:29`,
    `StorageReliabilityModule.cs:25`, `SensorsModule.cs:28`). The grey note keeps that visible
    without degrading the scan.
12. **The O3 temperature is read before the benchmark,** not under load (`HeadlessScan.cs:129-132`).
    The text says "earlier in this scan".
13. **History is keyed by Subject** (`HistoryStore.cs:100`), so two identical drives share a trend
    series. Advice uses no trend from a shared series (3.3): a count that grew on one of the two is
    graded by its own reading, and a Normal count is Check again, not Act now. The trend rows
    themselves still compare the two drives; keying history by disk number is the fix there, and is
    not Step B's.
14. **An elevated in-process headless scan** (`HeadlessScan.cs:86-89`) reads the elevating
    account's backups. The protection line always says "for the account this scan ran as"
    (principle 12).
15. **The GUI has no automated tests.** The logic lives in Core (Arrange, the store), and the XAML
    is covered by the ACCEPTANCE rows.
16. **Item order matters for S4,** because S1 reads H1's result. The engine fixes the order and a
    test pins it.

---

## 11. Owner questions, with defaults

1. **May an employee hide "Back up now: this drive is failing" with "Don't show again"?**
   *Default: yes.* It comes back as soon as the drive gets worse (higher count, higher urgency, a
   growth trend). The HTML, JSON, headless output and kit always show it, and the GUI's hidden line
   says when an "Act now" card is among the hidden ones. The alternative is that H1 can never be
   hidden.
2. **Should every other "Action needed" reading also get its own card?** These would be updates
   waiting, Defender definitions, the battery, and so on. *Default: no.* The section counts them
   ("N other readings…") and their tips stay in their rows. Cards for them would put 4-6 cards on
   every pilot laptop and on a freshly restored image, against the Phase 2 gate "none on the pilot
   laptops apart from the hardware ones" (`REMEDIES-PLAN.md:885`).
3. **Pull Step C row 3 (system drive type) into Step B,** so S1 can say "Windows runs from a hard
   disk"? *Default: no.* Step C row 3 adds it with its RAID and USB-box rules, and the label is one
   line once the reading exists.

---

## 12. Deliberately not in Step B

- **Fallback cards for every other Bad finding.** They duplicate row tips, and they would make the
  pilots and a fresh restore show cards for updates, Defender age and battery.
- **A live M6 or M5.** M6 needs M3's culprits. Without M3 the leak cannot be ruled out, and the plan
  carries only their text.
- **S4's hard-disk card or label, and the H4 reading.** Step C row 3.
- **New reads for O4** (domain-joined, policy keys). Step C. O4's IT line is always hedged instead.
- **The O3 throttle-event branch.** It waits for a band from healthy PCs (principle 14).
- **Pinned metric ids for Step C readings** (hibernation size, Downloads, caches, dwm). Step C names
  them.
- **A JSON rule language for advice.** The items are C#, the text is data.
- **Any button, any action, any change to the PC.** Phase 2.
- **Vietnamese.** Step E. The catalog is ready for it.
