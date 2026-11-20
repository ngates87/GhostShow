using System.Text.Json.Nodes;
using GhostShow.Core.Diagnostics;
using GhostShow.Core.Storage;

namespace GhostShow.Browser.Remote;

/// <summary>
/// How the show computer is running, for a device's Diagnostics screen: its report arrives as "diagnostics" messages,
/// about once a second while the screen is open (<see cref="SetWatching"/>).
/// </summary>
sealed class RemoteDiagnostics(RemoteLink link) : IDiagnostics
{
    bool _watching;

    public DiagnosticsReport Report { get; private set; } = DiagnosticsReport.Empty;

    public event Action? Changed;

    /// <summary>The Diagnostics screen opened or closed here: the show computer measures itself for it while it's open.</summary>
    public void SetWatching(bool watching)
    {
        if (watching == _watching) return;
        _watching = watching;
        link.Send("watchDiagnostics", ("on", watching));
    }

    /// <summary>After each hello (a new connection): watching again, if the screen is open.</summary>
    public void OnHello()
    {
        if (_watching) link.Send("watchDiagnostics", ("on", true));
    }

    public void OnReport(JsonObject message)
    {
        if (message["report"] is not { } node || ShowJson.FromNode<DiagnosticsReport>(node) is not { } report) return;
        if (report.SameAs(Report)) return;
        Report = report;
        Changed?.Invoke();
    }
}
