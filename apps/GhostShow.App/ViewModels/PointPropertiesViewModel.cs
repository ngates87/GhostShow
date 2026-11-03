using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>Exact values for one selected envelope handle. Every change is an undoable edit.</summary>
public sealed partial class PointPropertiesViewModel(CurvePoint point, ChannelLane lane, LightingClip clip, TimelineEditorViewModel editor) : ObservableObject
{
    public static IReadOnlyList<CurveKind> Curves { get; } = [CurveKind.Linear, CurveKind.Step, CurveKind.Spline];

    public CurvePoint Point { get; } = point;

    public string Channel => editor.LaneLabel(lane.Target);

    public string ClipName => clip.Name ?? LightingClip.DefaultName;

    /// <summary>An envelope's end handle stays at the clip's end: only its level changes, and it can't be deleted.</summary>
    public bool IsEndHandle => clip.IsEndHandle(Point);

    public bool CanEdit => !clip.Locked;
    public bool CanEditTime => CanEdit && !IsEndHandle;
    public bool CanDelete => CanEdit && !IsEndHandle;

    public string Heading => IsEndHandle ? "End handle" : "Envelope handle";

    /// <summary>On the timeline (the clip's start plus the handle's time in it); kept between its neighbours.</summary>
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

    /// <summary>How the level gets here from the previous handle.</summary>
    public CurveKind Curve
    {
        get => Point.Curve;
        set => editor.SetCurve([Point], value);
    }

    [RelayCommand]
    void Delete() => editor.DeleteCommand.Execute(null);

    public void Refresh() => OnPropertyChanged(string.Empty);
}
