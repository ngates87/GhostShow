using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;
using GhostShow.Core.Playback;

namespace GhostShow.App;

/// <summary>
/// What closing the app would stop, for the warning before it closes: fixtures that aren't blacked out (closing
/// blacks them out), timelines playing or paused, devices connected, and changes not saved yet.
/// </summary>
/// <param name="LitFixtures">The fixtures with any channel above 0, in patch order.</param>
/// <param name="OtherLitChannels">Channels above 0 that no fixture is patched on.</param>
public sealed record CloseWarning(string ShowName, bool HasUnsavedChanges, IReadOnlyList<string> LitFixtures, int OtherLitChannels,
                                  IReadOnlyList<string> Playing, IReadOnlyList<string> Paused, int Devices)
{
    /// <summary>Not blacked out, something running, or a device connected: closing now would be noticed.</summary>
    public bool IsLive => LitFixtures.Count > 0 || OtherLitChannels > 0 || Playing.Count > 0 || Paused.Count > 0 || Devices > 0;

    /// <summary>What's still live, a line each (none when it isn't).</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string>();
            if (LitFixtures.Count > 0)
                lines.Add($"Not blacked out: {Some(LitFixtures)}" + (OtherLitChannels > 0 ? $" (and {Channels(OtherLitChannels)} with no fixture)" : ""));
            else if (OtherLitChannels > 0)
                lines.Add($"Not blacked out: {Channels(OtherLitChannels)} with no fixture");
            if (Playing.Count > 0) lines.Add($"Playing: {Some(Playing)}");
            if (Paused.Count > 0) lines.Add($"Paused: {Some(Paused)}");
            if (Devices > 0) lines.Add(Devices == 1 ? "1 device is connected" : $"{Devices} devices are connected");
            return lines;
        }
    }

    /// <summary>What closing does about it.</summary>
    public string Consequence
    {
        get
        {
            if (!IsLive) return "Nothing is playing and every fixture is blacked out.";
            var does = new List<string>();
            if (Playing.Count > 0 || Paused.Count > 0) does.Add("stops every timeline and its sound");
            if (LitFixtures.Count > 0 || OtherLitChannels > 0) does.Add("blacks out every fixture");
            if (Devices > 0) does.Add(Devices == 1 ? "disconnects the device" : "disconnects the devices");
            return $"Closing {Join(does)}.";
        }
    }

    /// <summary>
    /// The show as it is now: the levels going out (every universe), what's running (the held lights aren't a timeline;
    /// they're in the levels), and how many devices are connected.
    /// </summary>
    public static CloseWarning Of(ShowEngine engine, IReadOnlyList<RunningTimeline> running, int devices, string showName, bool hasUnsavedChanges)
    {
        var lit = new List<string>();
        int other = 0;
        var levels = new byte[DmxUniverse.ChannelCount];
        var patched = new bool[DmxUniverse.ChannelCount];
        var fixtures = engine.Fixtures;
        foreach (int universe in engine.Universes)
        {
            if (!engine.TryCopyUniverse(universe, levels, out _)) continue;
            Array.Clear(patched);
            foreach (var fixture in fixtures.Where(f => f.Universe == universe))
            {
                bool on = false;
                for (int address = Math.Max(1, fixture.StartAddress); address <= Math.Min(DmxUniverse.ChannelCount, fixture.EndAddress); address++)
                {
                    patched[address - 1] = true;
                    on |= levels[address - 1] > 0;
                }
                if (on) lit.Add(fixture.Name);
            }
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] > 0 && !patched[i]) other++;
        }

        var timelines = running.Where(r => !r.IsHeld).ToList();
        var playing = timelines.Where(r => r.Status.State == TransportState.Playing).Select(r => r.Name).Distinct().ToList();
        var paused = timelines.Where(r => r.Status.State != TransportState.Playing).Select(r => r.Name).Distinct().Except(playing).ToList();
        return new CloseWarning(showName, hasUnsavedChanges, lit, other, playing, paused, devices);
    }

    /// <summary>"A", "A and B", "A, B and C", "A, B, C and 4 more".</summary>
    static string Some(IReadOnlyList<string> names) =>
        names.Count > 3 ? $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more" : Join(names);

    static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    static string Channels(int count) => count == 1 ? "1 channel" : $"{count} channels";
}
