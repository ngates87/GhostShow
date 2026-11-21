using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Diagnostics;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The status bar's load meter: how hard the show computer is working (<see cref="LoadReading"/>), on its own screen and
/// on every device's, which are sent its readings. Rises at once and falls back gently, like a hardware meter, so a
/// spike is seen.
/// </summary>
public sealed partial class PerformanceMeterViewModel : ObservableObject
{
    /// <summary>0-1, as drawn.</summary>
    [ObservableProperty]
    public partial double Level { get; private set; }

    [ObservableProperty]
    public partial string Tip { get; private set; } = LoadReading.Measuring.Tip;

    /// <summary>A reading from the show computer, a couple of times a second.</summary>
    public void Show(LoadReading reading)
    {
        Level = reading.Load >= Level ? reading.Load : Level - (Level - reading.Load) * 0.35;   // instant attack, gentle release
        Tip = reading.Tip;
    }
}
