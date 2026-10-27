using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>Exact values for one selected point of a lighting channel. Every change is an undoable edit.</summary>
public sealed partial class PointPropertiesViewModel(CurvePoint point, ChannelLane lane, TimelineEditorViewModel editor) : ObservableObject
{
    public CurvePoint Point { get; } = point;

    public string Channel => editor.LaneLabel(lane.Target);

    /// <summary>On the timeline (the block's start plus the point's time in it); kept within its block.</summary>
    public decimal TimeSeconds
    {
        get => Math.Round((decimal)editor.AbsoluteTime(Point).TotalSeconds, 3);
        set => editor.SetPoint(Point, time: TimeSpan.FromSeconds((double)value), field: "time");
    }

    /// <summary>The level as a percentage, the way consoles show it.</summary>
    public decimal Percent
    {
        get => Math.Round(Point.Value * 100m / 255, 1);
        set => editor.SetPoint(Point, value: (byte)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100), field: "percent");
    }

    public decimal Dmx
    {
        get => Point.Value;
        set => editor.SetPoint(Point, value: (byte)Math.Clamp(value, 0, 255), field: "dmx");
    }

    /// <summary>Unchecked: fade here from the previous point. Checked: hold the previous level, then jump here.</summary>
    public bool Step
    {
        get => Point.Step;
        set => editor.SetStep(Point, value);
    }

    [RelayCommand]
    void Delete() => editor.DeleteCommand.Execute(null);

    public void Refresh() => OnPropertyChanged(string.Empty);
}
