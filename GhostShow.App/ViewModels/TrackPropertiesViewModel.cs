using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// A track's properties in the right-hand panel (right-click its name → Properties…): its name and, for a lighting
/// track, VenueMagic's track options and each channel's starting level. Every change is an undoable edit.
/// </summary>
public sealed partial class TrackPropertiesViewModel : ObservableObject
{
    readonly TrackRowViewModel _row;
    readonly TimelineEditorViewModel _editor;
    readonly Action _close;

    public TrackPropertiesViewModel(TrackRowViewModel row, TimelineEditorViewModel editor, Action close)
    {
        _row = row;
        _editor = editor;
        _close = close;
        Channels = row.Track.Channels.Select(c => new ChannelStartViewModel(c, editor)).ToList();
    }

    public TimelineTrack Track => _row.Track;
    public bool IsLighting => _row.IsLighting;
    public string Heading => $"{_row.KindLabel} track";

    public string Name
    {
        get => _row.Name;
        set => _row.Name = value;
    }

    public bool DrawLevelsBetweenClips
    {
        get => Track.DrawLevelsBetweenClips;
        set => _editor.SetDrawLevelsBetweenClips(Track, value);
    }

    public IReadOnlyList<ChannelStartViewModel> Channels { get; private set; }

    public bool HasChannels => Channels.Count > 0;

    [RelayCommand]
    void EditChannels() => _editor.EditChannels(_row);

    [RelayCommand]
    void Close() => _close();

    /// <summary>After any edit (including undo): the fields show the model, and channels added or removed appear or go.</summary>
    public void Refresh()
    {
        if (!Channels.Select(c => c.Channel).SequenceEqual(Track.Channels))
        {
            Channels = Track.Channels.Select(c => new ChannelStartViewModel(c, _editor)).ToList();
            OnPropertyChanged(nameof(Channels));
            OnPropertyChanged(nameof(HasChannels));
        }
        foreach (var channel in Channels) channel.Refresh();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DrawLevelsBetweenClips));
    }
}

/// <summary>One channel of a lighting track: where it is before the first clip (VenueMagic's "starting level").</summary>
public sealed partial class ChannelStartViewModel(ChannelLane channel, TimelineEditorViewModel editor) : ObservableObject
{
    public static IReadOnlyList<string> Sources { get; } = ["Start of the first clip", "Fixed level"];

    public ChannelLane Channel { get; } = channel;
    public string Label => editor.LaneLabel(Channel.Target);
    public string Color => TimelineEditorViewModel.LaneColor(Channel);

    /// <summary>0: the timeline leaves the channel alone until its first clip, which starts at the level; 1: held at the level from the start.</summary>
    public int SourceIndex
    {
        get => Channel.Start == ChannelStart.Fixed ? 1 : 0;
        set => editor.SetChannelStart(Channel, value == 1 ? ChannelStart.Fixed : ChannelStart.FirstClip);
    }

    public decimal Percent
    {
        get => Math.Round(Channel.StartLevel * 100m / 255, 1);
        set => editor.SetStartLevel(Channel, (byte)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100), "percent");
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
