using Avalonia.Controls;

namespace GhostShow.App.Views;

public partial class OutputsView : UserControl
{
    public OutputsView()
    {
        InitializeComponent();
        UndoKeys.Listen(this, () => (DataContext as ViewModels.OutputsViewModel)?.History);
    }
}
