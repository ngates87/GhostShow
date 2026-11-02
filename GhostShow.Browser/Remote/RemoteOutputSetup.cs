using System.Text.Json.Nodes;
using GhostShow.Core.Dmx;
using GhostShow.Core.Storage;

namespace GhostShow.Browser.Remote;

/// <summary>
/// The show computer's DMX hardware, for the DMX setup screen here. It's looked for and run over there; this only shows
/// what the "outputs" messages say and passes on what's picked.
/// </summary>
sealed class RemoteOutputSetup(RemoteLink link) : IOutputSetup
{
    bool _watching;

    public OutputSetupState State { get; private set; } = OutputSetupState.Empty;

    public event Action? Changed;

    public void SetWatching(object screen, bool watching)
    {
        if (watching == _watching) return;
        _watching = watching;
        link.Send("watchOutputs", ("on", watching));
    }

    public Task ScanAgainAsync() => link.RequestAsync("scanOutputs");

    public Task<string?> AssignAsync(int universe, string choice) => link.RequestAsync("assignOutput", ("universe", universe), ("choice", choice));

    /// <summary>The whole show arrived (it may have reconnected): the show computer doesn't remember what this device was watching.</summary>
    public void OnHello()
    {
        if (_watching) link.Send("watchOutputs", ("on", true));
    }

    /// <summary>An "outputs" message: what the show computer's hardware looks like now.</summary>
    public void OnOutputs(JsonObject message)
    {
        State = ShowJson.FromNode<OutputSetupState>(message["state"]!) ?? OutputSetupState.Empty;
        Changed?.Invoke();
    }
}
