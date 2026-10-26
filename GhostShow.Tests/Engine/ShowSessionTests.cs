using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;
using static GhostShow.Tests.TestProfiles;

namespace GhostShow.Tests.Engine;

public class ShowSessionTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly ShowSession _session = new(new ShowEngine(), Library, NullLogger<ShowSession>.Instance);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Applying_patches_valid_fixtures_and_reports_the_rest()
    {
        var good = Fixture("Good", 1);
        _session.Project.Fixtures.AddRange([good, Fixture("Missing", 10, profileId: "nope")]);

        var result = _session.Apply();

        Assert.Equal(good.Id, Assert.Single(result.Fixtures).Id);
        Assert.Single(result.Issues);
        Assert.True(_session.Engine.SetFixtureControl(good.Id, ChannelFunction.Red, 10).Succeeded);
    }

    [Fact]
    public void Duplicate_universe_numbers_are_reported()
    {
        _session.Project.Universes.Add(new UniverseConfig { Number = 0, Name = "Again" });

        var result = _session.Apply();

        Assert.Contains(result.Issues, i => i.Message.Contains("defined 2 times"));
    }

    [Fact]
    public async Task Saved_show_reopens_with_the_same_patch()
    {
        var par = Fixture("Par", 5);
        _session.Project.Fixtures.Add(par);
        _session.Apply();
        string path = _dir.File("show.json");
        await _session.SaveAsync(path, TestContext.Current.CancellationToken);

        var reopened = new ShowSession(new ShowEngine(), Library, NullLogger<ShowSession>.Instance);
        var result = await reopened.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(path, reopened.FilePath);
        Assert.Equal(5, Assert.Single(reopened.Patch.Fixtures).StartAddress);
    }

    [Fact]
    public async Task Failing_to_open_keeps_the_current_show()
    {
        _session.Project.Name = "Current";
        string path = _dir.File("broken.json");
        await File.WriteAllTextAsync(path, "{ broken", TestContext.Current.CancellationToken);

        var result = await _session.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Error);
        Assert.Equal("Current", _session.Project.Name);
    }

    [Fact]
    public async Task Saving_while_output_reads_the_engine_does_not_disturb_it()
    {
        var engine = _session.Engine;
        using var stop = new CancellationTokenSource();
        long reads = 0;
        var reader = Task.Run(() =>
        {
            var frame = new byte[512];
            while (!stop.IsCancellationRequested) { engine.TryCopyUniverse(0, frame, out _); reads++; }
        }, TestContext.Current.CancellationToken);

        for (int i = 0; i < 20; i++) await _session.SaveAsync(_dir.File("show.json"), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await reader;

        Assert.True(reads > 0);
    }
}
