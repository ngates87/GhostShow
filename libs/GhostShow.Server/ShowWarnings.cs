using GhostShow.Core.Fixtures;
using GhostShow.Dmx;

namespace GhostShow.Server;

/// <summary>What needs attention on the show computer, in words: the toolbar's ⚠ list, on its screen and every device's.</summary>
public static class ShowWarnings
{
    /// <summary>Patch errors and warnings, failing DMX outputs, audio outputs that aren't connected, inputs that aren't listening.</summary>
    public static List<string> Collect(ShowServices services, LocalInputSetup inputs)
    {
        var warnings = new List<string>();
        var issues = services.Session.Patch.Issues;
        int errors = issues.Count(i => i.Severity == PatchIssueSeverity.Error);
        int overlaps = issues.Count - errors;
        if (errors > 0) warnings.Add($"{errors} fixture/universe errors: those fixtures aren't output. See Fixtures.");
        if (overlaps > 0) warnings.Add($"{overlaps} patch warnings (e.g. overlapping addresses). See Fixtures.");
        foreach (var service in services.Outputs.Services.Where(s => s.Status == DmxOutputStatus.Faulted))
            warnings.Add($"Universe {service.Universe}: {service.LastError} Retrying.");
        foreach (var output in services.Audio.Status.MissingOutputs)
            warnings.Add($"Audio output '{output}' isn't connected: its tracks play on the default output. See Audio.");
        warnings.AddRange(inputs.Warnings);
        return warnings;
    }
}
