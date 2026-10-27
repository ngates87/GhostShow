using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Raw channel control: a page of faders over consecutive addresses of the active universe. The view sets the page
/// size to however many faders fit the window (<see cref="SetPageSize"/>), and paging moves by a whole page.
/// </summary>
public sealed partial class ChannelsViewModel : ObservableObject
{
    readonly ShowEngine _engine;
    readonly bool[] _timelineControlled = new bool[512];
    readonly Action<string> _reportError;
    readonly byte[] _frame = new byte[512];
    int _universe = GhostShow.Core.Model.Project.FirstUniverse;

    public ChannelsViewModel(ShowEngine engine, Action<string> reportError)
    {
        _engine = engine;
        _reportError = reportError;
        RebuildFaders();
    }

    public ObservableCollection<FaderViewModel> Faders { get; } = new();

    /// <summary>How many faders are shown; set by the view from the available space.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxBankStart), nameof(RangeLabel))]
    public partial int PageSize { get; private set; } = 16;

    /// <summary>First address on the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeLabel))]
    public partial decimal BankStart { get; set; } = 1;

    /// <summary>The last start address that still shows a full page.</summary>
    public decimal MaxBankStart => DmxUniverse.LastAddress - PageSize + 1;

    public string RangeLabel => $"Channels {FirstAddress}-{FirstAddress + PageSize - 1} of {DmxUniverse.LastAddress}";

    int FirstAddress => (int)Math.Clamp(BankStart, 1, MaxBankStart);

    partial void OnBankStartChanged(decimal value) => RebuildFaders();

    /// <summary>Called by the view when its size changes. Keeps the first address, moving back only if the page would run past 512.</summary>
    public void SetPageSize(int faderCount)
    {
        int size = Math.Clamp(faderCount, 1, DmxUniverse.ChannelCount);
        if (size == PageSize) return;
        PageSize = size;
        if (BankStart > MaxBankStart) BankStart = MaxBankStart;   // rebuilds via OnBankStartChanged
        else RebuildFaders();
    }

    public void SetUniverse(int universe)
    {
        if (universe == _universe) return;
        _universe = universe;
        RebuildFaders();
    }

    [RelayCommand]
    void PreviousPage() => BankStart = Math.Max(1, BankStart - PageSize);

    [RelayCommand]
    void NextPage() => BankStart = Math.Min(MaxBankStart, BankStart + PageSize);

    [RelayCommand]
    void ZeroPage()
    {
        foreach (var fader in Faders) fader.Value = 0;
    }

    [RelayCommand]
    void ClearUniverse()
    {
        var result = _engine.ClearUniverse(_universe);
        if (!result.Succeeded) _reportError(result.Error!);
    }

    public void Tick()
    {
        if (!_engine.TryCopyUniverse(_universe, _frame, out _)) return;
        _engine.CopyTimelineControlled(_universe, _timelineControlled);
        foreach (var fader in Faders) fader.ShowValue(_frame[fader.Address - 1], _timelineControlled[fader.Address - 1]);
    }

    void RebuildFaders()
    {
        Faders.Clear();
        int start = FirstAddress;
        int universe = _universe;
        for (int address = start; address < start + PageSize; address++)
        {
            int a = address;
            Faders.Add(new FaderViewModel(a, a.ToString(), null, v => _engine.SetChannel(universe, a, v), _reportError));
        }
        Tick();
    }
}
