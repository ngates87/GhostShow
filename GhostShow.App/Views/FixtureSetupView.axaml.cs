using Avalonia.Controls;
using GhostShow.App.Controls;
using GhostShow.App.ViewModels;

namespace GhostShow.App.Views;

public partial class FixtureSetupView : UserControl
{
    public FixtureSetupView()
    {
        InitializeComponent();

        // The maps raise plain events (they're drawing controls, not lists); pass them to the view model.
        HeatMap.FixtureClicked += id => Map?.Select(id);

        OverviewMap.FixtureClicked += id => Map?.Select(id);
        OverviewMap.FixtureMoved += (id, start) => Map?.Move(id, start);
        ShowHover(OverviewMap);

        // While placing, clicks on openings add fixtures; clicks on existing fixtures do nothing (no leaving the step by accident).
        PlacementMap.PlaceRequested += start => Map?.Place(start);
        ShowHover(PlacementMap);
    }

    PatchMapViewModel? Map => (DataContext as FixtureSetupViewModel)?.Map;

    void ShowHover(PatchMap map) => map.HoverChanged += text => { if (Map is { } m) m.Hover = text ?? ""; };
}
