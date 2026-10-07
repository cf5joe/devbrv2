using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Backup;
using DevBR.Domain;

namespace DevBR.Restore.Execution;

/// <summary>The restore report: what was restored, how far it was verified, and what is left to do. Contains no setting values.</summary>
public static class RestoreReportWriter
{
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static (string Html, string Json) Write(RestoreRun run, BackupOverview overview, string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = run.CompletedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(folder, $"restore-{stamp}-{run.JobId.ToString("N")[..8]}");
        var report = Build(run, overview);

        var jsonPath = baseName + ".json";
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, Json));
        var htmlPath = baseName + ".html";
        File.WriteAllText(htmlPath, Html(report));
        return (htmlPath, jsonPath);
    }

    public static RestoreReport Build(RestoreRun run, BackupOverview overview)
    {
        var findings = run.Preflight.Findings.Where(f => f.Severity != FindingSeverity.Information || f.Prerequisite is "Sign-in" or "Administrator approval").ToList();
        return new RestoreReport(
            run.JobId, run.Outcome, run.Message, Path.GetFileName(overview.ArchivePath), overview.Manifest.ArchiveId, overview.Manifest.SourceMachineName,
            run.Preflight.Plan.PlanId, run.StartedAt, run.CompletedAt,
            new RestoreCounts(run.Count(RestoreStatus.Applied), run.Count(RestoreStatus.Skipped), run.Count(RestoreStatus.Failed), run.Count(RestoreStatus.Blocked),
                run.Operations.Count(o => o.Verification == VerificationLevel.FunctionallyVerified),
                run.Operations.Count(o => o.Verification == VerificationLevel.ConfigurationApplied),
                run.Operations.Count(o => o.Verification == VerificationLevel.VerificationFailed)),
            run.Operations,
            [.. findings.Select(f => new ReportFinding(f.Severity, f.Problem, f.WhyItMatters, f.NextSteps))],
            run.Preflight.Reinstall,
            run.CanRollBack);
    }

    private static string Html(RestoreReport r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        var b = new StringBuilder();
        b.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>DevBR restore report</title><style>
            :root{--bg:#fff;--fg:#1b1b1b;--muted:#5f5f5f;--line:#e3e3e3;--ok:#0f7b0f;--warn:#9d5d00;--bad:#c42b1c;--card:#f7f7f7}
            @media (prefers-color-scheme:dark){:root{--bg:#202020;--fg:#f3f3f3;--muted:#b0b0b0;--line:#3a3a3a;--ok:#6ccb5f;--warn:#fce100;--bad:#ff99a4;--card:#2b2b2b}}
            body{background:var(--bg);color:var(--fg);font:14px/1.5 "Segoe UI",system-ui,sans-serif;margin:0;padding:24px 16px;max-width:1100px;margin-inline:auto}
            h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 8px}.muted{color:var(--muted)}
            .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:8px;margin:16px 0}
            .card{background:var(--card);border-radius:8px;padding:10px 12px}.card b{display:block;font-size:22px}
            table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}
            th{font-weight:600}.Applied{color:var(--ok)}.Failed{color:var(--bad)}.Blocked{color:var(--warn)}.Skipped{color:var(--muted)}
            .wrap{overflow-x:auto}ul{margin:4px 0 0;padding-left:20px}
            </style></head><body>
            """);
        b.Append($"<h1>Restore report</h1><p class=\"muted\">{E(r.BackupFile)} (from {E(r.SourceMachine)}) · {r.StartedAt.ToLocalTime():g} – {r.CompletedAt.ToLocalTime():t} · job {r.JobId}</p>");
        b.Append($"<p><strong>{E(r.Message)}</strong></p>");
        b.Append("<div class=\"cards\">");
        foreach (var (label, value) in new[]
                 {
                     ("Restored", r.Counts.Applied), ("Verified working", r.Counts.FunctionallyVerified), ("Configuration applied", r.Counts.ConfigurationApplied),
                     ("Failed", r.Counts.Failed), ("Blocked", r.Counts.Blocked), ("Skipped", r.Counts.Skipped),
                 })
        {
            b.Append($"<div class=\"card\"><b>{value}</b>{E(label)}</div>");
        }

        b.Append("</div>");

        var attention = r.Operations.Where(o => o.Status is RestoreStatus.Failed or RestoreStatus.Blocked || o.Verification == VerificationLevel.VerificationFailed).ToList();
        if (attention.Count > 0 || r.Findings.Count > 0)
        {
            b.Append("<h2>What to do next</h2><ul>");
            foreach (var o in attention)
            {
                b.Append($"<li><strong>{E(o.ArtifactName)}: {E(o.Title)}</strong> – {E(o.Detail)}");
                if (o.NextSteps.Count > 0)
                {
                    b.Append("<ul>").Append(string.Concat(o.NextSteps.Select(s => $"<li>{E(s)}</li>"))).Append("</ul>");
                }

                b.Append("</li>");
            }

            foreach (var f in r.Findings)
            {
                b.Append($"<li>{E(f.Problem)} <span class=\"muted\">{E(f.WhyItMatters)}</span><ul>{string.Concat(f.NextSteps.Select(s => $"<li>{E(s)}</li>"))}</ul></li>");
            }

            b.Append("</ul>");
        }

        if (r.Reinstall.Count > 0)
        {
            b.Append("<h2>Tools to reinstall</h2><ul>");
            foreach (var g in r.Reinstall)
            {
                b.Append($"<li>{E(g.Name)}{(g.SourceVersion is null ? string.Empty : $" {E(g.SourceVersion)}")} – {E(g.Hint)}</li>");
            }

            b.Append("</ul>");
        }

        b.Append("<h2>All changes</h2><div class=\"wrap\"><table><tr><th>Item</th><th>Change</th><th>Result</th><th>Verification</th></tr>");
        foreach (var o in r.Operations)
        {
            b.Append($"<tr><td>{E(o.ArtifactName)}</td><td>{E(o.Title)}{(o.Detail is null ? string.Empty : $"<div class=\"muted\">{E(o.Detail)}</div>")}</td>")
             .Append($"<td class=\"{o.Status}\">{o.Status}</td><td>{E(Verification(o.Verification))}</td></tr>");
        }

        b.Append("</table></div>");
        b.Append(r.RollbackAvailable
            ? "<p class=\"muted\">You can undo the file and environment changes from DevBR (Restore › Recent restores › Roll back). Installed software is not removed.</p>"
            : string.Empty);
        b.Append("</body></html>");
        return b.ToString();
    }

    private static string Verification(VerificationLevel level) => level switch
    {
        VerificationLevel.FunctionallyVerified => "Verified working",
        VerificationLevel.ConfigurationApplied => "Configuration applied",
        VerificationLevel.VerificationFailed => "Check failed",
        _ => "Not verified",
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record RestoreCounts(int Applied, int Skipped, int Failed, int Blocked, int FunctionallyVerified, int ConfigurationApplied, int VerificationFailed);

public sealed record ReportFinding(FindingSeverity Severity, string Problem, string WhyItMatters, IReadOnlyList<string> NextSteps);

public sealed record RestoreReport(
    Guid JobId,
    RestoreRunOutcome Outcome,
    string Message,
    string BackupFile,
    Guid ArchiveId,
    string SourceMachine,
    Guid PlanId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    RestoreCounts Counts,
    IReadOnlyList<OperationReport> Operations,
    IReadOnlyList<ReportFinding> Findings,
    IReadOnlyList<ReinstallGuidance> Reinstall,
    bool RollbackAvailable);
