using Avalonia.Data.Converters;

namespace GhostShow.App.ViewModels;

public static class Converters
{
    /// <summary>Dims disabled fixtures in lists.</summary>
    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
