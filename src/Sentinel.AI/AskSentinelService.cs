using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Privacy;
using Sentinel.Core.Settings;
using Sentinel.Diagnostics;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Telemetry;

namespace Sentinel.AI;

public sealed record AskAnswer(ParsedQuery Query, DiagnosticResult Result, string? AiText, string? AiModel, string? AiNote, TimeSpan Elapsed);

/// <summary>
/// Ask Sentinel: question → intent and time range → structured local query → deterministic analysis →
/// optional local-model explanation. The model only ever receives the redacted evidence for this question,
/// never the database, and it has no tools: its text is displayed, never executed.
/// </summary>
public sealed class AskSentinelService(DiagnosticsEngine diagnostics, OllamaClient ollama, ISettingsStore settings, ProviderSet providers, ILogger<AskSentinelService> log)
{
    public const string SystemPrompt = """
        You are the explanation layer of Sentinel, a read-only PC health monitor. You receive structured evidence that
        Sentinel's deterministic analysis produced for one user question. Your job is to explain it clearly.

        Rules:
        - Use ONLY the evidence provided. Never invent readings, numbers, times, causes, component names or events.
        - Keep the four labels exactly and in this order, omitting a section only if it would be empty:
          **Observed:** facts measured by telemetry.
          **Inferred:** conclusions the evidence supports.
          **Possible:** plausible contributors that were not measured.
          **Unknown:** what cannot be determined.
        - If the evidence is insufficient, say "I don't have enough evidence to determine the cause."
        - Never claim a component is defective unless the evidence explicitly says so.
        - Never tell the user to run commands, scripts, PowerShell, registry edits, or to download software.
          You may mention the recommended actions provided, which use Windows Settings or the manufacturer's app.
        - Be calm and precise. No alarmist language. Under 200 words. Plain text with the bold labels only.
        """;

    public async Task<AskAnswer> AskAsync(string question, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTimeOffset.Now;
        var parsed = QueryParser.Parse(question, now, providers.System.Inventory.BootTime);
        if (parsed.Intent == QueryIntent.Navigate)
            parsed = parsed with { Intent = QueryIntent.Overview };

        var result = await Task.Run(() => diagnostics.Run(parsed), ct).ConfigureAwait(false);
        var s = settings.Current;
        if (!s.OllamaEnabled || string.IsNullOrWhiteSpace(s.OllamaModel))
            return new AskAnswer(parsed, result, null, null, null, sw.Elapsed);

        try
        {
            var text = await ollama.ChatAsync(s.OllamaEndpoint, s.OllamaModel!, SystemPrompt, BuildEvidencePrompt(result), ct).ConfigureAwait(false);
            return new AskAnswer(parsed, result, Sanitize(text), s.OllamaModel, null, sw.Elapsed);
        }
        catch (OllamaException ex)
        {
            // Never log the conversation itself; only that the explanation step failed.
            log.LogInformation("Local AI explanation unavailable: {Message}", ex.Message);
            return new AskAnswer(parsed, result, null, s.OllamaModel, ex.Message + " Showing Sentinel's own analysis instead.", sw.Elapsed);
        }
    }

    /// <summary>The exact (redacted) payload sent to the local model. Exposed so the UI can show it on request.</summary>
    public static string BuildEvidencePrompt(DiagnosticResult r)
    {
        var payload = new
        {
            question = Redactor.Redact(r.Question),
            timeRange = r.Range.Label,
            title = r.Title,
            summary = Redactor.Redact(r.Summary),
            confidence = r.Confidence.Label(),
            evidence = r.Findings.Select(f => new { kind = f.Kind.Label(), statement = Redactor.Redact(f.Text), source = f.Source }),
            facts = r.Facts.Select(f => new { label = f.Label, value = Redactor.Redact(f.Value) }),
            recommendedActions = r.Actions.Select(a => a.Text),
        };
        var sb = new StringBuilder();
        sb.AppendLine("Evidence from Sentinel (JSON):");
        sb.AppendLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
        sb.Append("Explain this to the user, following the rules.");
        return sb.ToString();
    }

    /// <summary>Removes code blocks and shell-like lines so a model cannot present runnable instructions.</summary>
    internal static string Sanitize(string text)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var output = new List<string>();
        var inCode = false;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode) continue;
            if (t.StartsWith("PS ", StringComparison.Ordinal) || t.StartsWith("C:\\>", StringComparison.OrdinalIgnoreCase) || t.StartsWith("$ ", StringComparison.Ordinal)
                || t.StartsWith("reg ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("powershell", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("sudo ", StringComparison.OrdinalIgnoreCase))
                continue;
            output.Add(line);
        }
        return string.Join('\n', output).Trim();
    }
}
