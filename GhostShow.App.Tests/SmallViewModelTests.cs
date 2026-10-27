using GhostShow.App.ViewModels;
using GhostShow.Core.Engine;

namespace GhostShow.App.Tests;

public class ModeChoiceTests
{
    [Fact]
    public void Starts_on_the_named_mode_or_the_first()
    {
        Assert.Equal("3ch", new ModeChoiceViewModel(TestRig.Par, "3ch").Selected.Name);
        Assert.Equal("5ch", new ModeChoiceViewModel(TestRig.Par, "nope").Selected.Name);
    }

    [Fact]
    public void Selecting_one_mode_deselects_the_others_and_reports_the_change()
    {
        string? reported = null;
        var choice = new ModeChoiceViewModel(TestRig.Par, "5ch", m => reported = m.Name);

        choice.Options[1].IsSelected = true;

        Assert.Equal([false, true], choice.Options.Select(o => o.IsSelected));
        Assert.Equal("3ch", reported);
        Assert.Equal("5ch · 5 ch", choice.Options[0].Label);
    }
}

public class ChannelsPagingTests
{
    readonly ShowEngine _engine = new();
    readonly ChannelsViewModel _channels;

    public ChannelsPagingTests() => _channels = new ChannelsViewModel(_engine, _ => { });

    [Fact]
    public void Page_size_follows_the_space_available()
    {
        _channels.SetPageSize(40);

        Assert.Equal(40, _channels.Faders.Count);
        Assert.Equal("Channels 1-40 of 512", _channels.RangeLabel);
    }

    [Fact]
    public void Paging_moves_by_a_whole_page_and_stops_at_512()
    {
        _channels.SetPageSize(100);

        for (int i = 0; i < 10; i++) _channels.NextPageCommand.Execute(null);

        Assert.Equal("Channels 413-512 of 512", _channels.RangeLabel);
    }

    [Fact]
    public void Growing_the_page_near_the_end_moves_back_rather_than_running_past_512()
    {
        _channels.SetPageSize(10);
        _channels.BankStart = 500;

        _channels.SetPageSize(40);

        Assert.Equal("Channels 473-512 of 512", _channels.RangeLabel);
    }

    [Fact]
    public void Faders_write_the_engine_and_show_changes_made_elsewhere()
    {
        _channels.SetPageSize(16);

        _channels.Faders[2].Value = 200;
        _engine.SetChannel(1, 4, 99);
        _channels.Tick();

        var frame = new byte[512];
        _engine.TryCopyUniverse(1, frame, out _);
        Assert.Equal(200, frame[2]);
        Assert.Equal(99, _channels.Faders[3].Value);
    }
}
