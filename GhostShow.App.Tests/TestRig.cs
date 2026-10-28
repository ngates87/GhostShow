using GhostShow.App.ViewModels;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>A show session with a small, known profile library, and the fixture setup screen on top of it.</summary>
sealed class TestRig : IDisposable
{
    public static readonly FixtureProfile Par = Profile("Acme", "PAR", "Color Changer", ("5ch", 5), ("3ch", 3));
    public static readonly FixtureProfile Wash = Profile("Acme", "Wash", "Color Changer", ("10ch", 10));
    public static readonly FixtureProfile Fogger = Profile("acme", "Fogger", "Smoke", ("1ch", 1));   // lower-case brand on purpose
    public static readonly FixtureProfile Dimmer = Profile("Other", "Dimmer", "Dimmer", ("1ch", 1));

    readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "ghostshow-app-tests", Guid.NewGuid().ToString("N"));

    public TestRig()
    {
        Library = new FixtureProfileLibrary([Par, Wash, Fogger, Dimmer]);
        Session = new ShowSession(new ShowEngine(), Library, NullLogger<ShowSession>.Instance);
        Paths = new AppPaths(_configDirectory);
        var libraryViewModel = new ProfileLibraryViewModel(Session, Paths, new MachineSettings(), NullLogger.Instance);
        Setup = new FixtureSetupViewModel(Session, libraryViewModel, () => Dirty = true);
    }

    public FixtureProfileLibrary Library { get; }

    /// <summary>A config folder of its own (custom fixture types are saved under it), deleted afterwards.</summary>
    public AppPaths Paths { get; }
    public ShowSession Session { get; }
    public FixtureSetupViewModel Setup { get; }
    public bool Dirty { get; private set; }

    /// <summary>Runs the wizard: Add fixtures → pick the profile and mode → Next.</summary>
    public void ChooseTypeToAdd(FixtureProfile profile, string mode)
    {
        Setup.StartAddCommand.Execute(null);
        Setup.Browser.Search = profile.Model;
        Setup.Browser.OpenCommand.Execute(Setup.Browser.Tiles.Single(t => t.Profile == profile));
        Setup.Browser.Pending!.Options.Single(o => o.Mode.Name == mode).IsSelected = true;
        Setup.Browser.ApplyCommand.Execute(null);
    }

    public IReadOnlyList<(string Name, int Start, int End)> Patched() =>
        Session.Patch.Fixtures.OrderBy(f => f.StartAddress).Select(f => (f.Name, f.StartAddress, f.EndAddress)).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory)) Directory.Delete(_configDirectory, recursive: true);
    }

    static FixtureProfile Profile(string manufacturer, string model, string type, params (string Name, int Channels)[] modes) => new()
    {
        Id = $"test/{manufacturer}/{model}",
        Manufacturer = manufacturer,
        Model = model,
        Type = type,
        Modes = modes.Select(m => new FixtureMode
        {
            Name = m.Name,
            Channels = Enumerable.Range(1, m.Channels).Select(i => new FixtureChannel { Name = $"Ch {i}" }).ToArray(),
        }).ToArray(),
    };
}
