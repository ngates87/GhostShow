using Avalonia.Data.Converters;

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
}
