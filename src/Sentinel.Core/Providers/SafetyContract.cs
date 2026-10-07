namespace Sentinel.Core.Providers;

public sealed class SafetyContractViolationException(string providerId, string clause)
    : Exception($"Provider '{providerId}' violates the Sentinel safety contract: {clause}")
{
    public string ProviderId { get; } = providerId;
    public string Clause { get; } = clause;
}

/// <summary>
/// The formal Sentinel safety contract (SAFETY.md §Contract). A provider that cannot attest to every
/// clause is refused at registration time. This is a structural guard, not a substitute for review.
/// </summary>
public static class SafetyContract
{
    public static readonly IReadOnlyList<string> Clauses =
    [
        "1. Read-only.",
        "2. No hardware state modification.",
        "3. No security degradation.",
        "4. No unsupported kernel driver installation.",
        "5. No firmware interaction.",
        "6. No arbitrary privileged commands.",
        "7. Failure cannot destabilize the host.",
        "8. User can disable provider.",
        "9. Provider reports provenance.",
        "10. Unsupported hardware fails gracefully.",
    ];

    public static void Validate(ProviderDescriptor d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var s = d.Safety;
        if (!s.ReadOnly) throw new SafetyContractViolationException(d.Id, Clauses[0]);
        if (s.ModifiesHardwareState) throw new SafetyContractViolationException(d.Id, Clauses[1]);
        if (s.DegradesSecurity) throw new SafetyContractViolationException(d.Id, Clauses[2]);
        if (s.InstallsKernelDriver) throw new SafetyContractViolationException(d.Id, Clauses[3]);
        if (s.InteractsWithFirmware) throw new SafetyContractViolationException(d.Id, Clauses[4]);
        if (s.RunsArbitraryPrivilegedCommands) throw new SafetyContractViolationException(d.Id, Clauses[5]);
        if (!s.FailureIsolated) throw new SafetyContractViolationException(d.Id, Clauses[6]);
        if (!s.UserCanDisable) throw new SafetyContractViolationException(d.Id, Clauses[7]);
        if (!s.ReportsProvenance || d.DataSources.Count == 0) throw new SafetyContractViolationException(d.Id, Clauses[8]);
        if (!s.FailsGracefullyOnUnsupportedHardware) throw new SafetyContractViolationException(d.Id, Clauses[9]);
    }
}
