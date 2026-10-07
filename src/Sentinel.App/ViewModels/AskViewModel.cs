using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.AI;
using Sentinel.App.Controls;
using Sentinel.Core.Settings;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed record ActionLink(string Text, Uri? Uri, string? Label)
{
    public bool HasUri => Uri is not null;
}

public sealed record AnswerCard(
    string Question,
    string Title,
    string Summary,
    string Confidence,
    HealthStatus ConfidenceStatus,
    string RangeLabel,
    string? AiText,
    string AiLabel,
    string? AiNote,
    IReadOnlyList<EvidenceRow> Findings,
    IReadOnlyList<InfoItem> Facts,
    IReadOnlyList<ActionLink> Actions,
    string EvidencePrompt,
    string Timing)
{
    public bool HasAi => !string.IsNullOrWhiteSpace(AiText);
    public bool HasAiNote => !string.IsNullOrWhiteSpace(AiNote);
    public bool HasFacts => Facts.Count > 0;
}

public sealed partial class AskViewModel : PageViewModel
{
    private readonly AskSentinelService _ask = App.Services.GetRequiredService<AskSentinelService>();
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();

    protected override int RefreshEveryTicks => 10;

    public ObservableCollection<AnswerCard> Answers { get; } = [];

    public IReadOnlyList<string> Suggestions { get; } =
    [
        "Why did my laptop become hot at 2 PM?",
        "Why did my battery drain quickly today?",
        "Has my SSD health changed?",
        "Was my gaming performance worse this week?",
        "What caused yesterday's restart?",
        "Is my laptop behaving normally?",
        "Why is memory usage high?",
        "What changed after my NVIDIA driver update?",
        "Which applications consume the most resources over time?",
        "Is my battery deteriorating faster than before?",
    ];

    [ObservableProperty] public partial string Question { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string AiStatus { get; set; } = "";

    protected override void OnActivated(object? parameter)
    {
        if (parameter is string q && !string.IsNullOrWhiteSpace(q))
        {
            Question = q;
            _ = AskAsync();
        }
    }

    protected override void Refresh()
    {
        var s = _settings.Current;
        AiStatus = s.OllamaEnabled && s.OllamaModel is not null
            ? $"Explanations by local model {s.OllamaModel} (on this PC). Sentinel's own analysis is always shown alongside."
            : "Answers come from Sentinel's deterministic analysis of local telemetry. Optional local AI can be enabled in Settings.";
    }

    [RelayCommand]
    private void UseSuggestion(string text)
    {
        Question = text;
        _ = AskAsync();
    }

    [RelayCommand]
    private async Task AskAsync()
    {
        var q = Question.Trim();
        if (q.Length == 0 || IsBusy) return;
        IsBusy = true;
        try
        {
            var a = await _ask.AskAsync(q, CancellationToken.None);
            var r = a.Result;
            var status = r.Confidence switch { Domain.Confidence.High => HealthStatus.Good, Domain.Confidence.Moderate => HealthStatus.Normal, _ => HealthStatus.Unknown };
            Answers.Insert(0, new AnswerCard(q, r.Title, r.Summary, r.Confidence.Label(), status, $"Looked at {r.Range.Label}",
                a.AiText, a.AiModel is null ? "" : $"Explanation by local model {a.AiModel} — based only on the evidence below", a.AiNote,
                r.Findings.Select(f => new EvidenceRow(f.Kind, f.Text, f.Source)).ToList(),
                r.Facts.Select(f => new InfoItem(f.Label, f.Value, f.Source)).ToList(),
                r.Actions.Select(x => new ActionLink(x.Text, x.SettingsUri is null ? null : new Uri(x.SettingsUri), x.SettingsLabel)).ToList(),
                AskSentinelService.BuildEvidencePrompt(r),
                $"Answered in {a.Elapsed.TotalSeconds:F1} s"));
            Question = "";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
