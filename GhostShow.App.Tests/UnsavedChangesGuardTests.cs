namespace GhostShow.App.Tests;

public class UnsavedChangesGuardTests
{
    sealed class Answer(SaveChoice choice) : ISaveChangesPrompt
    {
        public int Asked;
        public Task<SaveChoice> AskAsync(string showName, string action)
        {
            Asked++;
            return Task.FromResult(choice);
        }
    }

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
}
