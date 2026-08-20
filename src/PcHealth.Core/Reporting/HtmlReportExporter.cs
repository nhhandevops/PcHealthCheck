using System.Globalization;
using System.Text;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;

namespace PcHealth.Core.Reporting;

/// <summary>
/// Self-contained single-file HTML report (inline CSS, no external references,
/// no scripts). Written only to a user-chosen local path.
/// </summary>
public static class HtmlReportExporter
{
    public static void WriteTo(ScanReport report, string path) => File.WriteAllText(path, ToHtml(report), Encoding.UTF8);

    public static string ToHtml(ScanReport report)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PC Health Report</title>
            <style>
            :root { color-scheme: light dark; }
            body { font-family: "Segoe UI", system-ui, sans-serif; margin: 0; padding: 24px; background: #f5f6f8; color: #1a1c1f; }
            @media (prefers-color-scheme: dark) { body { background: #17191c; color: #e8eaed; } .card, table { background: #212428 !important; } th { background: #2a2e33 !important; } }
            h1 { font-size: 1.5rem; margin: 0 0 4px; }
            h2 { font-size: 1.1rem; margin: 28px 0 8px; }
            .meta { opacity: .75; font-size: .85rem; margin-bottom: 20px; }
            .cards { display: flex; flex-wrap: wrap; gap: 12px; }
            .card { background: #fff; border-radius: 10px; padding: 14px 18px; min-width: 150px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
            .card .cat { font-size: .8rem; opacity: .7; }
            .card .grade { font-size: 1.15rem; font-weight: 600; }
            .chip { display: inline-block; border-radius: 999px; padding: 1px 10px; font-size: .78rem; font-weight: 600; }
            .g-Good { background: #d9f2df; color: #135c26; }
            .g-Normal { background: #fdf0d3; color: #7a5a00; }
            .g-Bad { background: #fadcdc; color: #8f1d1d; }
            .g-Unknown { background: #e6e8eb; color: #4a4f55; }
            table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 10px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
            th, td { text-align: left; padding: 8px 12px; font-size: .86rem; vertical-align: top; border-top: 1px solid rgba(128,128,128,.15); }
            th { background: #eceef1; border-top: none; }
            .tips { margin: 4px 0 0; padding-left: 18px; }
            .tips li { margin: 2px 0; }
            .wrap { overflow-x: auto; }
            .footer { margin-top: 32px; font-size: .8rem; opacity: .7; }
            code { font-size: .8rem; word-break: break-all; }
            </style>
            </head>
            <body>
            """);

        sb.Append("<h1>PC Health Report ");
        sb.Append(GradeChip(report.OverallGrade));
        sb.Append("</h1>\n<div class=\"meta\">");
        sb.Append(E($"{report.MachineName} — {report.OsDescription} — generated {report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} by PcHealthCheck {report.AppVersion}"));
        sb.Append(report.RanElevated ? " (elevated scan)" : " (standard scan, no admin)");
        sb.Append("</div>\n");

        sb.Append("<div class=\"cards\">\n");
        foreach (var (category, grade) in report.CategoryGrades.OrderBy(kv => kv.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><div class=\"cat\">{E(CategoryLabel(category))}</div><div class=\"grade\">{GradeChip(grade)}</div></div>\n");
        }
        sb.Append("</div>\n");

        // Absence of data is not absence of problems: a check that could not finish must
        // never be read as "nothing wrong here", so it is called out before the results.
        var incomplete = report.Modules
            .Where(m => m.Status is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted)
            .ToList();
        if (incomplete.Count > 0)
        {
            sb.Append("<div class=\"card\" style=\"border-left:4px solid #b8860b;margin:16px 0\">");
            sb.Append("<strong>Some checks could not finish.</strong> Their results are missing from this report — " +
                      "that is not the same as being healthy:<ul class=\"tips\">");
            foreach (var module in incomplete)
            {
                var why = module.Error ?? (module.Notes.Count > 0 ? string.Join("; ", module.Notes) : "no reason given");
                sb.Append(CultureInfo.InvariantCulture, $"<li><strong>{E(module.ModuleName)}</strong> — {E(why)}</li>");
            }
            sb.Append("</ul></div>\n");
        }

        if (report.Trends.Count > 0)
        {
            sb.Append("<h2>What changed since your last scan</h2>\n<div class=\"wrap\"><table>\n");
            sb.Append("<tr><th>Status</th><th>Measurement</th><th>Drive</th><th>Change</th></tr>\n");
            foreach (var trend in report.Trends.OrderBy(t => t.Grade switch { Grade.Bad => 0, Grade.Normal => 1, _ => 2 }))
            {
                sb.Append("<tr>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{GradeChip(trend.Grade)}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(trend.Label)}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(trend.Subject ?? "—")}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(trend.Message)}");
                if (trend.Tips.Count > 0)
                {
                    sb.Append("<ul class=\"tips\">");
                    foreach (var tip in trend.Tips) sb.Append(CultureInfo.InvariantCulture, $"<li>{E(tip)}</li>");
                    sb.Append("</ul>");
                }
                sb.Append("</td></tr>\n");
            }
            sb.Append("</table></div>\n");
        }
        else if (report.History is { EarlierScans: > 0 } coverage)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<p><em>Nothing has moved: every measurement matches the {coverage.EarlierScans} earlier scan(s) {coverage.SpanText}.</em></p>\n");
        }
        else
        {
            sb.Append("<p><em>This is the first recorded scan, so there is nothing to compare against yet. " +
                      "Run the app again in a week or two — a counter that is <strong>growing</strong> is far more " +
                      "informative than any single reading.</em></p>\n");
        }

        foreach (var group in report.Verdicts.GroupBy(v => v.Metric.Category).OrderBy(g => g.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h2>{E(CategoryLabel(group.Key))}</h2>\n<div class=\"wrap\"><table>\n");
            sb.Append("<tr><th>Status</th><th>Check</th><th>Subject</th><th>Value</th><th>Details &amp; tips</th></tr>\n");
            foreach (var v in group.OrderBy(v => v.Grade switch { Grade.Bad => 0, Grade.Normal => 1, Grade.Good => 2, _ => 3 }))
            {
                sb.Append("<tr>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{GradeChip(v.Grade, v.Metric.InfoOnly)}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(v.Metric.Label)}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(v.Metric.Subject ?? "—")}</td>");
                sb.Append(CultureInfo.InvariantCulture, $"<td>{E(v.Metric.Value.ToString())}{(v.Metric.Unit is null ? "" : " " + E(v.Metric.Unit))}</td>");
                sb.Append("<td>");
                if (v.Metric.Detail is not null) sb.Append(E(v.Metric.Detail));
                if (v.Tips.Count > 0)
                {
                    sb.Append("<ul class=\"tips\">");
                    foreach (var tip in v.Tips) sb.Append(CultureInfo.InvariantCulture, $"<li>{E(tip)}</li>");
                    sb.Append("</ul>");
                }
                if (v.Forecast is not null) sb.Append(CultureInfo.InvariantCulture, $"<div><em>{E(v.Forecast)}</em></div>");
                sb.Append("</td></tr>\n");
            }
            sb.Append("</table></div>\n");
        }

        sb.Append("<h2>Modules run</h2>\n<div class=\"wrap\"><table>\n<tr><th>Module</th><th>Status</th><th>Duration</th><th>Notes</th></tr>\n");
        foreach (var m in report.Modules)
        {
            var notes = string.Join("; ", m.Notes);
            if (m.Error is not null) notes = string.IsNullOrEmpty(notes) ? m.Error : $"{notes}; {m.Error}";
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(m.ModuleName)}</td><td>{E(m.Status.ToString())}</td><td>{m.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s</td><td>{E(notes)}</td></tr>\n");
        }
        sb.Append("</table></div>\n");

        sb.Append("<h2>Receipt — everything this scan touched</h2>\n<div class=\"wrap\"><table>\n<tr><th>Time</th><th>Module</th><th>Action</th></tr>\n");
        foreach (var entry in report.Receipt)
        {
            var action = entry switch
            {
                ProcessReceipt p => $"Ran process: <code>{E(p.FileName)} {E(p.Arguments)}</code> (exit {(p.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?")})",
                FileWriteReceipt f => $"Wrote file: <code>{E(f.Path)}</code> ({f.Bytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} bytes, {E(f.Purpose)}){(f.Deleted ? " — deleted afterwards" : "")}",
                _ => E(entry.ToString() ?? ""),
            };
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{entry.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}</td><td>{E(entry.ModuleId)}</td><td>{action}</td></tr>\n");
        }
        if (report.Receipt.Count == 0)
        {
            sb.Append("<tr><td colspan=\"3\">No external processes were run and no files were written.</td></tr>\n");
        }
        sb.Append("</table></div>\n");
        // Scoped to the scan on purpose, and true by construction: no check in this app
        // contains network code. The one opt-in lookup lives outside scans entirely, and
        // saying so here is better than a blanket claim a later feature could falsify.
        sb.Append("<p><strong>Network connections made by PcHealthCheck during this scan: 0.</strong> " +
                  "No check in this app contains network code. The app has no telemetry, no crash reporting, " +
                  "and no phone-home — ever. Its one internet feature, the optional VirusTotal second opinion, " +
                  "runs only when you start it with your own API key, and is not part of any scan or report.</p>\n");

        sb.Append("""
            <div class="footer">
            This report was generated locally and saved only to the location you chose. Green results are not a guarantee —
            roughly a quarter of drive failures occur with no prior SMART warning. Keep backups: 3 copies, 2 different media, 1 off-site.
            </div>
            </body>
            </html>
            """);
        return sb.ToString();
    }

    private static string GradeChip(Grade grade, bool infoOnly = false)
    {
        var label = infoOnly ? "Info" : grade switch
        {
            Grade.Good => "Good",
            Grade.Normal => "Normal",
            Grade.Bad => "Action needed",
            _ => "Not measured",
        };
        return $"<span class=\"chip g-{grade}\">{label}</span>";
    }

    private static string CategoryLabel(HealthCategory category) => category switch
    {
        HealthCategory.SoftwareOs => "Software / OS",
        _ => category.ToString(),
    };

    /// <summary>Minimal HTML encoder — kept local because the framework's WebUtility lives in a
    /// namespace our no-network CI gate forbids outside the opt-in assembly.</summary>
    private static string E(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
