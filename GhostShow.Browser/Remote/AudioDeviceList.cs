using System.Text.Json.Nodes;
using GhostShow.Browser.Remote;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The browser build's version of the desktop's AudioDeviceList (which needs the sound card): the show computer's
/// audio devices, as it reports them, for audio tracks' outputs. A test tone plays on the show computer.
/// </summary>
public sealed class AudioDeviceList
{
    readonly RemoteLink _link;

    internal AudioDeviceList(RemoteLink link) => _link = link;

    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Raised (on the UI thread) when <see cref="Names"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The show computer sends its list when it changes: nothing to ask.</summary>
    public Task RefreshAsync() => Task.CompletedTask;

    /// <summary>Asks the show computer for a test tone on a device (null: its default output).</summary>
    public bool PlayTestSound(string? device)
    {
        _link.Send("testSound", ("device", device));
        return true;
    }

    /// <summary>From a "hello" or "audioDevices" message (on the UI thread).</summary>
    internal void Update(JsonArray? devices)
    {
        Names = [.. (devices ?? []).Select(d => d!.GetValue<string>())];
        Changed?.Invoke();
    }

    /// <summary>The same choices the desktop offers (see the desktop's AudioDeviceList.TrackChoices).</summary>
    public static IReadOnlyList<AudioDeviceChoice> TrackChoices(IReadOnlyList<string> names, string? current)
    {
        var choices = new List<AudioDeviceChoice> { new("Default output", null) };
        choices.AddRange(names.Select(n => new AudioDeviceChoice(n, n)));
        if (current is not null && !names.Contains(current)) choices.Add(new AudioDeviceChoice($"{current} (not connected)", current));
        return choices;
    }
}

public sealed record AudioDeviceChoice(string Label, string? Name)
{
    public override string ToString() => Label;
}
