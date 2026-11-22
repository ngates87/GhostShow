namespace GhostShow.App.Tests;

public class UnsavedChangesGuardTests
{
    sealed class Answer(SaveChoice choice) : ISaveChangesPrompt
    {
        public int Asked;
        public CloseWarning? Warned;

        public Task<SaveChoice> AskAsync(string showName, string action)
        {
            Asked++;
            return Task.FromResult(choice);
        }

        public Task<SaveChoice> AskCloseAsync(CloseWarning warning)
        {
            Warned = warning;
            return Task.FromResult(choice);
        }
    }

    static CloseWarning Warning(bool unsaved) => new("haunt.json", unsaved, [], 0, [], [], 0);

    bool _dirty = true;
    bool _saveSucceeds = true;
    int _saves;

    UnsavedChangesGuard Guard(ISaveChangesPrompt prompt) => new(() => _dirty, () => "haunt.json", () =>
    {
        _saves++;
        if (_saveSucceeds) _dirty = false;
        return Task.CompletedTask;
    }, prompt);

    [Fact]
    public async Task Nothing_unsaved_proceeds_without_asking()
    {
        _dirty = false;
        var prompt = new Answer(SaveChoice.Cancel);

        Assert.True(await Guard(prompt).ConfirmAsync("closing"));
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public async Task Save_saves_then_proceeds()
    {
        Assert.True(await Guard(new Answer(SaveChoice.Save)).ConfirmAsync("closing"));
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task Save_that_did_not_happen_does_not_proceed()
    {
        _saveSucceeds = false;   // e.g. the user cancelled the Save As dialog, or the disk was full

        Assert.False(await Guard(new Answer(SaveChoice.Save)).ConfirmAsync("closing"));
    }

    [Fact]
    public async Task Dont_save_proceeds_without_saving()
    {
        Assert.True(await Guard(new Answer(SaveChoice.DontSave)).ConfirmAsync("closing"));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Cancel_stops_the_action()
    {
        Assert.False(await Guard(new Answer(SaveChoice.Cancel)).ConfirmAsync("closing"));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Closing_asks_even_with_nothing_unsaved()
    {
        _dirty = false;
        var prompt = new Answer(SaveChoice.Cancel);

        Assert.False(await Guard(prompt).ConfirmCloseAsync(Warning(unsaved: false)));
        Assert.NotNull(prompt.Warned);
        Assert.True(await Guard(new Answer(SaveChoice.DontSave)).ConfirmCloseAsync(Warning(unsaved: false)));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Save_and_close_saves_first_and_stays_open_if_it_did_not_save()
    {
        Assert.True(await Guard(new Answer(SaveChoice.Save)).ConfirmCloseAsync(Warning(unsaved: true)));
        Assert.Equal(1, _saves);

        _dirty = true;
        _saveSucceeds = false;
        Assert.False(await Guard(new Answer(SaveChoice.Save)).ConfirmCloseAsync(Warning(unsaved: true)));
    }

    [Fact]
    public void The_warning_says_what_is_still_live_and_what_closing_does()
    {
        var dark = Warning(unsaved: false);
        Assert.False(dark.IsLive);
        Assert.Empty(dark.Lines);
        Assert.Equal("Nothing is playing and every fixture is blacked out.", dark.Consequence);

        var live = new CloseWarning("haunt.json", false, ["Fog", "Entry LED", "Spot", "Strobe", "Coffin"], 3, ["Main"], ["Scare", "Ambient"], 2);
        Assert.True(live.IsLive);
        Assert.Equal([
            "Not blacked out: Fog, Entry LED, Spot and 2 more (and 3 channels with no fixture)",
            "Playing: Main",
            "Paused: Scare and Ambient",
            "2 devices are connected",
        ], live.Lines);
        Assert.Equal("Closing stops every timeline and its sound, blacks out every fixture and disconnects the devices.", live.Consequence);

        var held = new CloseWarning("haunt.json", false, [], 1, [], [], 0);   // only a channel no fixture is on
        Assert.Equal(["Not blacked out: 1 channel with no fixture"], held.Lines);
        Assert.Equal("Closing blacks out every fixture.", held.Consequence);
    }
}
