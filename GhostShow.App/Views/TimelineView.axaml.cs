using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GhostShow.App.ViewModels;
using GhostShow.Core.Playback;

namespace GhostShow.App.Views;

/// <summary>
/// Keyboard shortcuts, zooming with Ctrl+wheel around the pointer, keeping the playhead in view while playing, and the
/// timeline tree's mouse and keyboard handling (open, rename in place, drag to move). Everything else is bound to
/// <see cref="TimelineEditorViewModel"/>.
/// </summary>
public partial class TimelineView : UserControl
{
    static readonly DataFormat<TimelineTreeNode> TreeNodeFormat = DataFormat.CreateInProcessFormat<TimelineTreeNode>("ghostshow/timeline-tree-node");
    const double DragThreshold = 6;

    TimelineEditorViewModel? _editor;
    PointerPressedEventArgs? _pressed;
    TimelineTreeNode? _pressedNode;
    Point _pressedAt;

    public TimelineView()
    {
        InitializeComponent();
        // Tunnel, so Space plays and pauses even when a button has focus (which would otherwise "click" it).
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        LanesScroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        TimelineTree.DoubleTapped += (_, e) =>
        {
            if (NodeAt(e.Source) is { IsEditing: false }) _editor?.Tree.OpenCommand.Execute(null);
        };
        TimelineTree.AddHandler(KeyDownEvent, OnTreeKey, RoutingStrategies.Tunnel);
        TimelineTree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel);
        TimelineTree.AddHandler(PointerMovedEvent, OnTreeMoved, RoutingStrategies.Tunnel);
        TimelineTree.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);
        TimelineTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        TimelineTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        // The in-place rename box: focus it when it appears, commit when focus leaves it.
        TimelineTree.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { DataContext: TimelineTreeNode { IsEditing: true } node }) _editor?.Tree.CommitRename(node);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        TimelineTree.AddHandler(TextBox.LoadedEvent, (_, _) => FocusRenameBox(), RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_editor is not null)
        {
            _editor.Playback.PropertyChanged -= OnPlaybackChanged;
            _editor.Tree.PropertyChanged -= OnTreeChanged;
        }
        _editor = DataContext as TimelineEditorViewModel;
        if (_editor is not null)
        {
            _editor.Playback.PropertyChanged += OnPlaybackChanged;
            _editor.Tree.PropertyChanged += OnTreeChanged;
        }
    }

    // ---- the timeline tree ----

    static TimelineTreeNode? NodeAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<TimelineTreeNode>().FirstOrDefault();

    void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A rename can start from a command (F2, the menu, a new item): focus the box once it's shown.
        if (e.PropertyName == nameof(TimelineTreeViewModel.SelectedNode)) Avalonia.Threading.Dispatcher.UIThread.Post(FocusRenameBox);
    }

    void FocusRenameBox()
    {
        var box = TimelineTree.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.DataContext is TimelineTreeNode { IsEditing: true });
        if (box is null || box.IsFocused) return;
        box.Focus();
        box.SelectAll();
    }

    void OnTreeKey(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } editor) return;
        var tree = editor.Tree;
        if (e.Source is TextBox { DataContext: TimelineTreeNode { IsEditing: true } editing })
        {
            if (e.Key == Key.Enter) tree.CommitRename(editing);
            else if (e.Key == Key.Escape) tree.CancelRename(editing);
            else return;
            TimelineTree.Focus();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.F2: tree.StartRenameCommand.Execute(null); Avalonia.Threading.Dispatcher.UIThread.Post(FocusRenameBox); break;
            case Key.Delete: tree.AskDeleteCommand.Execute(null); break;
            case Key.Enter: tree.OpenCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedNode = NodeAt(e.Source);
        _pressed = _pressedNode is { IsEditing: false } && e.GetCurrentPoint(TimelineTree).Properties.IsLeftButtonPressed ? e : null;
        _pressedAt = e.GetPosition(TimelineTree);
    }

    /// <summary>Starts dragging a node once the pointer has moved a little (so clicks and double-clicks stay clicks).</summary>
    async void OnTreeMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _pressedNode is not { } node) return;
        var at = e.GetPosition(TimelineTree);
        if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold) return;
        _pressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TreeNodeFormat, node));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
    }

    /// <summary>Dropping on a folder puts it in that folder; on a timeline, in that timeline's folder; on empty space, at the top level.</summary>
    static string TargetFolderOf(object? source) => NodeAt(source) switch
    {
        TimelineFolderNode folder => folder.Path,
        { } node => node.ParentFolder,
        _ => "",
    };

    void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetValue(TreeNodeFormat) is not null ? DragDropEffects.Move : DragDropEffects.None;
    }

    void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (_editor is null || e.DataTransfer.TryGetValue(TreeNodeFormat) is not { } node) return;
        _editor.Tree.Move(node, TargetFolderOf(e.Source));
        e.Handled = true;
    }

    void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } editor || !IsEffectivelyVisible) return;
        // Typing in a field: leave its keys alone (Delete, Ctrl+C and Space mean what they do in text). The tree has
        // its own keys (Delete, F2, Enter).
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox) return;
        if (focused is Visual visual && visual.GetSelfAndVisualAncestors().Contains(TimelineTree) && e.Key != Key.Space) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? action = (e.Key, ctrl, shift) switch
        {
            (Key.Space, false, _) => () => editor.Playback.PlayPauseCommand.Execute(null),
            (Key.Z, true, false) => () => editor.UndoCommand.Execute(null),
            (Key.Z, true, true) or (Key.Y, true, _) => () => editor.RedoCommand.Execute(null),
            (Key.C, true, _) => () => editor.CopyCommand.Execute(null),
            (Key.V, true, _) => () => editor.PasteCommand.Execute(null),
            (Key.D, true, _) => () => editor.DuplicateCommand.Execute(null),
            (Key.A, true, _) => () => editor.SelectAllCommand.Execute(null),
            (Key.Delete or Key.Back, false, _) => () => editor.DeleteCommand.Execute(null),
            (Key.Escape, false, _) => editor.ClearSelection,
            _ => null,
        };
        if (action is null) return;
        action();
        e.Handled = true;
    }

    void OnZoomToFit(object? sender, RoutedEventArgs e) => _editor?.ZoomToFit(LanesScroll.Viewport.Width);

    /// <summary>Ctrl+wheel zooms, keeping the time under the pointer where it is.</summary>
    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_editor is not { } editor || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        double pointerInView = e.GetPosition(LanesScroll).X;
        double seconds = e.GetPosition(Canvas).X / editor.PixelsPerSecond;
        if (e.Delta.Y > 0) editor.ZoomInCommand.Execute(null);
        else editor.ZoomOutCommand.Execute(null);
        LanesScroll.UpdateLayout();
        LanesScroll.Offset = LanesScroll.Offset.WithX(Math.Max(0, seconds * editor.PixelsPerSecond - pointerInView));
        e.Handled = true;
    }

    /// <summary>While playing, scroll so the playhead stays visible (it jumps to near the left edge when it runs off).</summary>
    void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaybackViewModel.Position) || _editor?.Playback.State != TransportState.Playing || !IsEffectivelyVisible) return;
        double x = PlayheadLine.X, left = LanesScroll.Offset.X, width = LanesScroll.Viewport.Width;
        if (width <= 0 || (x >= left && x <= left + width - 20)) return;
        LanesScroll.Offset = LanesScroll.Offset.WithX(Math.Max(0, x - width * 0.1));
    }
}
