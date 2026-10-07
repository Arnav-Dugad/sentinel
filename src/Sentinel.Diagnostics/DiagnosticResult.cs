using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.Diagnostics;

public sealed record Finding(EvidenceKind Kind, string Text, string? Source = null, Severity Severity = Severity.Info);

public sealed record Fact(string Label, string Value, string? Source = null);

/// <summary>The deterministic answer to a diagnostic question, before any optional AI rephrasing.</summary>
public sealed record DiagnosticResult(
    string Question,
    QueryIntent Intent,
    TimeRange Range,
    string Title,
    string Summary,
    Confidence Confidence,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<RecommendedAction> Actions)
{
    public IEnumerable<Finding> Of(EvidenceKind kind) => Findings.Where(f => f.Kind == kind);

    public static DiagnosticResult NotEnoughEvidence(string question, QueryIntent intent, TimeRange range, string what) =>
        new(question, intent, range, "Not enough evidence", $"I don't have enough evidence to determine {what}.", Confidence.Low,
            [new Finding(EvidenceKind.Unknown, $"Sentinel has no recorded data that answers this for {range.Label}. History builds up while Sentinel runs.")], [], []);
}
