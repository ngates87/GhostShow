using Avalonia;
using Avalonia.Media;
using Avalonia.Data.Converters;
using GhostShow.App.Controls;

namespace GhostShow.App.ViewModels;

public static class Converters
{
    /// <summary>Dims disabled fixtures in lists.</summary>
    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);

    /// <summary>True when the bound page is the page given as the parameter; shows one screen at a time.</summary>
    public static readonly IValueConverter PageIs =
        new FuncValueConverter<Page, Page, bool>((current, page) => current == page);

    /// <summary>True when the bound section index is the section given as the parameter.</summary>
    public static readonly IValueConverter SectionIs =
        new FuncValueConverter<int, Section, bool>((current, section) => current == (int)section);

    /// <summary>The setup guide: green tick for a done step, grey ring for one to do.</summary>
    public static readonly IValueConverter DoneBrush =
        new FuncValueConverter<bool, IBrush>(done => done ? Brushes.LimeGreen : Brushes.Gray);

    public static readonly IValueConverter DoneOpacity = new FuncValueConverter<bool, double>(done => done ? 0.6 : 1.0);

    public static readonly IValueConverter BoldWhen = new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>An icon from Icons.axaml by the end of its key: "Usb" is Icon.Usb.</summary>
    public static readonly IValueConverter Icon = new FuncValueConverter<string?, Geometry?>(name =>
        name is not null && Application.Current?.TryGetResource($"Icon.{name}", null, out var icon) == true ? icon as Geometry : null);

    /// <summary>Hardware found on this computer: a free ring, or a grey dot when something already has it.</summary>
    public static readonly IValueConverter FoundState = new FuncValueConverter<bool, DeviceState>(inUse => inUse ? DeviceState.InUse : DeviceState.Available);
}
