using GhostShow.App;

namespace GhostShow.App.Tests;

/// <summary>Only one GhostShow runs at a time; a second start asks the running one to come forward.</summary>
public class SingleInstanceTests
{
    // A name of their own, so these never collide with a GhostShow running on the same computer.
    static string NewName() => $"GhostShowTest-{Guid.NewGuid():N}";

    [Fact]
    public void A_second_copy_cannot_claim_it_until_the_first_has_finished()
    {
        string name = NewName();
        var first = SingleInstance.TryClaim(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryClaim(name));

        first.Dispose();
        using var next = SingleInstance.TryClaim(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void A_second_copy_asks_the_running_one_to_show_itself()
    {
        string name = NewName();
        using var running = SingleInstance.TryClaim(name)!;
        using var shown = new ManualResetEventSlim();
        running.ShowRequested += shown.Set;

        Assert.True(SingleInstance.AskRunningToShow(name, TimeSpan.FromSeconds(5)));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // And again: it keeps listening after the first.
        shown.Reset();
        Assert.True(SingleInstance.AskRunningToShow(name, TimeSpan.FromSeconds(5)));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Asking_when_none_is_running_gives_up_after_the_timeout()
    {
        Assert.False(SingleInstance.AskRunningToShow(NewName(), TimeSpan.FromMilliseconds(200)));
    }
}
