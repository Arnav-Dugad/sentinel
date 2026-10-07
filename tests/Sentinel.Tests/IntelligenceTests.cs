using Sentinel.AI;
using Sentinel.Core.Privacy;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Diagnostics;
using Sentinel.Diagnostics.Reports;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Xunit;

namespace Sentinel.Tests;

public class QueryParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 16, 30, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData("Why did my laptop become hot at 2 PM?", QueryIntent.Heat)]
    [InlineData("Why did my battery drain quickly today?", QueryIntent.BatteryDrain)]
    [InlineData("Has my SSD health changed?", QueryIntent.Storage)]
    [InlineData("Was my gaming performance worse this week?", QueryIntent.Gaming)]
    [InlineData("What caused yesterday's restart?", QueryIntent.Restart)]
    [InlineData("Is my laptop behaving normally?", QueryIntent.Overview)]
    [InlineData("Why is memory usage high?", QueryIntent.Memory)]
    [InlineData("What changed after my NVIDIA driver update?", QueryIntent.WhatChanged)]
    [InlineData("Which applications consume the most resources over time?", QueryIntent.TopApps)]
    [InlineData("Is my battery deteriorating faster than before?", QueryIntent.BatteryHealth)]
    [InlineData("why did chrome keep crashing", QueryIntent.Crashes)]
    [InlineData("wifi keeps disconnecting", QueryIntent.Network)]
    public void IntentsFromTheSpecExamples(string text, QueryIntent intent) => Assert.Equal(intent, QueryParser.Parse(text, Now).Intent);

    [Fact]
    public void AtTimeProducesPointInTime()
    {
        var q = QueryParser.Parse("Why did my laptop become hot at 2 PM?", Now);
        Assert.True(q.RangeExplicit);
        Assert.Equal(14, q.PointInTime!.Value.Hour);
        Assert.True(q.Range.Contains(q.PointInTime.Value));
    }

    [Fact]
    public void YesterdayAndLastN()
    {
        var y = QueryParser.Parse("what caused yesterday's restart", Now);
        Assert.Equal(TimeRange.StartOfDay(Now).AddDays(-1), y.Range.From);
        var n = QueryParser.Parse("show battery last 7 days", Now);
        Assert.Equal(QueryIntent.Navigate, n.Intent);
        Assert.Equal("Battery", n.NavigateTarget);
        Assert.Equal(TimeSpan.FromDays(7), n.Range.Span);
    }

    [Fact]
    public void HangDoesNotMatchInsideChanged() => Assert.NotEqual(QueryIntent.Crashes, QueryParser.Parse("Has my SSD health changed?", Now).Intent);

    [Fact]
    public void SubjectVendorIsExtracted() => Assert.Equal("nvidia", QueryParser.Parse("What changed after my NVIDIA driver update?", Now).Subject);
}

public class NetworkEventTests
{
    [Fact]
    public void SleepRelatedDisconnectsAreExcluded()
    {
        var t = DateTimeOffset.Now;
        var events = new List<SystemEvent>
        {
            new(t, EventCategory.NetworkDisconnected, Severity.Notice, "Wi-Fi disconnected", "Reason: The network is disconnected by the driver.", "x", "Wi-Fi"),
            new(t.AddHours(1), EventCategory.NetworkDisconnected, Severity.Notice, "Wi-Fi disconnected", "Reason: system going to sleep", "x", "Wi-Fi"),
            new(t.AddHours(2), EventCategory.NetworkDisconnected, Severity.Notice, "Wi-Fi disconnected", null, "x", "Wi-Fi"),
            new(t.AddHours(2).AddMinutes(1), EventCategory.Sleep, Severity.Info, "Entered sleep", null, "x"),
        };
        var genuine = NetworkEvents.GenuineDisconnects(events, events);
        Assert.Single(genuine);
        Assert.Equal(t, genuine[0].Timestamp);
    }
}

public class CorrelationTests
{
    [Fact]
    public async Task StopErrorAndRestartAreConfirmed()
    {
        using var t = new TempStore();
        var at = DateTimeOffset.Now.AddHours(-2);
        var bug = new SystemEvent(at, EventCategory.Bugcheck, Severity.Critical, "Stop error", null, "x", null, "b", Code: "0x0000009F");
        var restart = new SystemEvent(at.AddMinutes(1), EventCategory.UnexpectedShutdown, Severity.Warning, "Unexpected shutdown", "Windows restarted. A stop error (bugcheck 0x0000009F) was recorded.", "x", null, "u");
        var game = new SystemEvent(at.AddMinutes(-10), EventCategory.WorkloadSession, Severity.Info, "Game", null, "x", null, "g");
        await t.Store.InsertEventsAsync([bug, restart, game], TestContext.Current.CancellationToken);
        var stored = t.Store.QueryEvents(at.AddHours(-1), at.AddHours(1));
        var engine = new CorrelationEngine(t.Store, UnitFormatter.Default);
        var ctx = engine.Explain(stored.First(e => e.Category == EventCategory.UnexpectedShutdown));
        Assert.Equal(CorrelationStrength.Confirmed, ctx.Related.First().Strength);
        Assert.Contains(ctx.Related, r => r.Related.Category == EventCategory.WorkloadSession && r.Strength == CorrelationStrength.Possible);
    }

    [Fact]
    public async Task RepeatedFailuresAfterDriverChangeAreStrongNotConfirmed()
    {
        using var t = new TempStore();
        var now = DateTimeOffset.Now;
        var events = new List<SystemEvent> { new(now.AddDays(-3), EventCategory.DriverChanged, Severity.Info, "NVIDIA driver 32.0 installed", null, "x", null, "d") };
        for (var i = 0; i < 4; i++)
            events.Add(new SystemEvent(now.AddDays(-2).AddHours(i), EventCategory.DisplayDriverReset, Severity.Warning, "TDR", null, "x", null, "tdr" + i));
        await t.Store.InsertEventsAsync(events, TestContext.Current.CancellationToken);
        var last = t.Store.QueryEvents(now.AddDays(-10), now, [EventCategory.DisplayDriverReset]).First();
        var ctx = new CorrelationEngine(t.Store, UnitFormatter.Default).Explain(last);
        var driver = ctx.Related.First(r => r.Related.Category == EventCategory.DriverChanged);
        Assert.Equal(CorrelationStrength.Strong, driver.Strength);
    }
}

public class PrivacyTests
{
    [Fact]
    public void RedactorRemovesIdentifiers()
    {
        var text = $"user {Environment.UserName} at C:\\Users\\someone\\docs on 192.168.1.20 mac 00-1A-2B-3C-4D-5E id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 mail a.b@example.com";
        var r = Redactor.Redact(text);
        Assert.DoesNotContain("192.168.1.20", r, StringComparison.Ordinal);
        Assert.DoesNotContain("00-1A-2B-3C-4D-5E", r, StringComparison.Ordinal);
        Assert.DoesNotContain("3f2504e0", r, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", r, StringComparison.Ordinal);
        Assert.DoesNotContain("a.b@example.com", r, StringComparison.Ordinal);
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName, r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriverVersionsAreNotMistakenForIps() => Assert.Contains("32.0.101.6790", Redactor.Redact("driver 32.0.101.6790"), StringComparison.Ordinal);

    [Fact]
    public void MaskKeepsOnlySuffix()
    {
        Assert.Equal("••••••••5678", Redactor.Mask("ABCDEFGH12345678"));
        Assert.Equal("—", Redactor.Mask(null));
    }

    [Fact]
    public void UntrustedStringsAreBounded() => Assert.True(Redactor.SanitizeUntrusted(new string('x', 5000), 100).Length <= 101);
}

public class SafetyContractTests
{
    private static ProviderDescriptor D(SafetyAttestation s, params string[] sources) =>
        new("p", "P", "Test", sources.Length == 0 ? ["source"] : sources, AccessRequirement.None, SamplingCost.Low, "1 s", s);

    [Fact]
    public void ReadOnlyObserverPasses() => SafetyContract.Validate(D(SafetyAttestation.ReadOnlyObserver));

    [Theory]
    [InlineData("write")]
    [InlineData("hardware")]
    [InlineData("security")]
    [InlineData("driver")]
    [InlineData("firmware")]
    [InlineData("commands")]
    public void ViolationsAreRejected(string clause)
    {
        var s = clause switch
        {
            "write" => SafetyAttestation.ReadOnlyObserver with { ReadOnly = false },
            "hardware" => SafetyAttestation.ReadOnlyObserver with { ModifiesHardwareState = true },
            "security" => SafetyAttestation.ReadOnlyObserver with { DegradesSecurity = true },
            "driver" => SafetyAttestation.ReadOnlyObserver with { InstallsKernelDriver = true },
            "firmware" => SafetyAttestation.ReadOnlyObserver with { InteractsWithFirmware = true },
            _ => SafetyAttestation.ReadOnlyObserver with { RunsArbitraryPrivilegedCommands = true },
        };
        Assert.Throws<SafetyContractViolationException>(() => SafetyContract.Validate(D(s)));
    }

    [Fact]
    public void ProvenanceIsRequired() =>
        Assert.Throws<SafetyContractViolationException>(() => SafetyContract.Validate(new ProviderDescriptor("p", "P", "x", [], AccessRequirement.None, SamplingCost.Low, "", SafetyAttestation.ReadOnlyObserver)));
}

public class AiSafetyTests
{
    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://192.168.1.10:11434", false)]
    [InlineData("https://ollama.example.com", false)]
    [InlineData("file:///c:/x", false)]
    public void OnlyLoopbackEndpointsAreAllowed(string endpoint, bool ok) => Assert.Equal(ok, OllamaClient.IsLoopback(endpoint, out _));

    [Fact]
    public void ModelOutputCannotPresentRunnableCommands()
    {
        var text = "**Observed:** fine.\n```powershell\nSet-ItemProperty HKLM:\\x -Value 0\n```\nPS C:\\> Stop-Process -Name x\nreg add HKLM\\x\n**Inferred:** ok";
        var clean = AskSentinelService.Sanitize(text);
        Assert.DoesNotContain("Set-ItemProperty", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("reg add", clean, StringComparison.Ordinal);
        Assert.Contains("**Inferred:** ok", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidencePromptIsRedacted()
    {
        var r = new DiagnosticResult("why is 192.168.0.5 slow", QueryIntent.Slowdown, TimeRange.Today(DateTimeOffset.Now), "Slow", "Seen at C:\\Users\\bob\\x", Confidence.Low,
            [new Finding(EvidenceKind.Observed, "MAC 00-11-22-33-44-55 busy")], [], []);
        var prompt = AskSentinelService.BuildEvidencePrompt(r);
        Assert.DoesNotContain("192.168.0.5", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("00-11-22-33-44-55", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\\Users\\bob", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3.8B", ModelTier.Lightweight)]
    [InlineData("8B", ModelTier.Balanced)]
    [InlineData("14B", ModelTier.Advanced)]
    [InlineData("500M", ModelTier.Lightweight)]
    public void ModelTiers(string size, ModelTier tier) => Assert.Equal(tier, OllamaClient.Classify(size, 0));
}

public class ReportTests
{
    [Fact]
    public void CsvNeutralisesFormulaInjectionAndHtmlEncodes()
    {
        var doc = new ReportDocument { Title = "T <script>", Kind = ReportKind.Drivers, PrivacySafe = false };
        var s = doc.Add("Section");
        var t = new ReportTable { Title = "Rows", Columns = ["A"] };
        t.Rows.Add(["=HYPERLINK(\"http://x\")"]);
        s.Tables.Add(t);
        var csv = ReportRenderer.Csv(doc);
        Assert.Contains("\"'=HYPERLINK", csv, StringComparison.Ordinal);
        var html = ReportRenderer.Html(doc);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacySafeReportsAreRedacted()
    {
        var doc = new ReportDocument { Title = "Report", Kind = ReportKind.SystemHealth, PrivacySafe = true };
        doc.Add("Net").Facts.Add(("Gateway", "10.0.0.1"));
        Assert.DoesNotContain("10.0.0.1", ReportRenderer.Json(doc), StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", ReportRenderer.Html(doc), StringComparison.Ordinal);
    }
}

public class UnitFormatterTests
{
    [Fact]
    public void FormatsUnits()
    {
        var store = new FixedSettings();
        var u = new UnitFormatter(store);
        Assert.Equal("1.0 KiB", u.Bytes(1024).Replace(',', '.'));
        Assert.EndsWith("°C", u.Temperature(50), StringComparison.Ordinal);
        store.Current.Temperature = TemperatureUnit.Fahrenheit;
        Assert.StartsWith("122", u.Temperature(50), StringComparison.Ordinal);
        store.Current.Throughput = ThroughputUnit.BitsPerSecond;
        Assert.EndsWith("Mbps", u.Throughput(1_250_000), StringComparison.Ordinal);
        Assert.Equal("—", u.Temperature(null));
        Assert.Equal("2h 5m", UnitFormatter.Duration(TimeSpan.FromMinutes(125)));
    }

    private sealed class FixedSettings : ISettingsStore
    {
        public SentinelSettings Current { get; } = new();
        public event EventHandler<SentinelSettings>? Changed { add { } remove { } }
        public void Update(Action<SentinelSettings> mutate) => mutate(Current);
    }
}
