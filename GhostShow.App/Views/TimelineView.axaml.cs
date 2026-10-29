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
    static DataFormat<TimelineTreeNode> TreeNodeFormat => Controls.TimelineCanvas.TreeNodeFormat;   // shared: a timeline can be dropped on the lanes
    const double DragThreshold = 6;

    TimelineEditorViewModel? _editor;
    EnvelopeEditorWindow? _envelopeWindow;
    PointerPressedEventArgs? _pressed;
    TimelineTreeNode? _pressedNode;
    Point _pressedAt;

    public TimelineView()
    {
        InitializeComponent();
        // Tunnel, so the editor's shortcuts (Delete, arrows, Ctrl+C...) get there before a focused control uses the key.
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

        // Track names: double-click to rename in place; Enter or leaving the box saves, Esc cancels.
        TrackHeaders.AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (e.Source is TextBlock { Classes: var classes, DataContext: TrackRowViewModel row } && classes.Contains("trackName")) StartTrackRename(row);
        });
        TrackHeaders.AddHandler(KeyDownEvent, OnTrackHeaderKey, RoutingStrategies.Tunnel);
        TrackHeaders.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { DataContext: TrackRowViewModel { IsRenaming: true } row }) row.CommitRename();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    void StartTrackRename(TrackRowViewModel row)
    {
        row.StartRenameCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var box = TrackHeaders.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.DataContext == row && t.IsEffectivelyVisible);
            box?.Focus();
            box?.SelectAll();
        });
    }

    void OnTrackHeaderKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox { DataContext: TrackRowViewModel { IsRenaming: true } renaming })
        {
            if (e.Key == Key.Enter) renaming.CommitRename();
            else if (e.Key == Key.Escape) renaming.CancelRename();
            else return;
            TrackHeaders.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _editor?.SelectedTrack is { } selected)
        {
            StartTrackRename(selected);
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_editor is not null)
        {
            _editor.Playback.PropertyChanged -= OnPlaybackChanged;
            _editor.Tree.PropertyChanged -= OnTreeChanged;
            _editor.PropertyChanged -= OnEditorChanged;
            _editor.EnvelopeEditorRequested -= ShowEnvelopeEditor;
        }
        _editor = DataContext as TimelineEditorViewModel;
        if (_editor is not null)
        {
            _editor.Playback.PropertyChanged += OnPlaybackChanged;
            _editor.Tree.PropertyChanged += OnTreeChanged;
            _editor.PropertyChanged += OnEditorChanged;
            _editor.EnvelopeEditorRequested += ShowEnvelopeEditor;
        }
    }

    /// <summary>The Envelope Editor's window: opened (owned by the main window, so it stays in front of it) or brought forward.</summary>
    void ShowEnvelopeEditor()
    {
        if (_editor?.EnvelopeEditor is not { } envelopes) return;
        if (_envelopeWindow is { } open)
        {
            open.Activate();
            return;
        }
        var window = _envelopeWindow = new EnvelopeEditorWindow { DataContext = envelopes };
        window.Closed += (_, _) =>
        {
            _envelopeWindow = null;
            _editor?.CloseEnvelopeEditor();
        };
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    /// <summary>The window, while open (for tests).</summary>
    public EnvelopeEditorWindow? EnvelopeWindow => _envelopeWindow;

    /// <summary>A timeline just opened (or switched to): show all of it.</summary>
    void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineEditorViewModel.EnvelopeEditor) && _editor?.EnvelopeEditor is null) _envelopeWindow?.Close();
        if (e.PropertyName != nameof(TimelineEditorViewModel.SelectedTimeline)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_editor is { SelectedTimeline: not null } editor && LanesScroll.Viewport.Width > 0) editor.ZoomToFit(LanesScroll.Viewport.Width);
        }, Avalonia.Threading.DispatcherPriority.Background);
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
        data.Add(DataTransferItem.Create(DataFormat.Text, node.Name));   // something the desktop's drag protocol can carry too
        Controls.TimelineCanvas.DraggedNode = node;
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move | DragDropEffects.Link);   // Link: onto the lanes, as an event
        }
        finally
        {
            Controls.TimelineCanvas.DraggedNode = null;
        }
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
        e.DragEffects = Controls.TimelineCanvas.NodeIn(e.DataTransfer) is not null ? DragDropEffects.Move : DragDropEffects.None;
    }

    void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (_editor is null || Controls.TimelineCanvas.NodeIn(e.DataTransfer) is not { } node) return;
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
        if (focused is Visual visual && (visual.GetSelfAndVisualAncestors().Contains(TimelineTree) || visual.GetSelfAndVisualAncestors().Contains(TrackHeaders))) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? action = (e.Key, ctrl, shift) switch
        {
            (Key.Left, false, _) => () => editor.Nudge(-1, 0, fine: shift),
            (Key.Right, false, _) => () => editor.Nudge(+1, 0, fine: shift),
            (Key.Up, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(0, +1, fine: shift),
            (Key.Down, false, _) when editor.SelectedPoints.Count > 0 => () => editor.Nudge(0, -1, fine: shift),
            (Key.Z, true, false) => () => editor.UndoCommand.Execute(null),
            (Key.Z, true, true) or (Key.Y, true, _) => () => editor.RedoCommand.Execute(null),
            (Key.C, true, _) => () => editor.CopyCommand.Execute(null),
            (Key.V, true, _) => () => editor.PasteCommand.Execute(null),
            (Key.D, true, _) => () => editor.DuplicateCommand.Execute(null),
            (Key.A, true, _) => () => editor.SelectAllCommand.Execute(null),
            (Key.Delete or Key.Back, false, _) => () => editor.DeleteCommand.Execute(null),
            (Key.Escape, false, _) => editor.ClearSelection,
            (Key.Home, false, _) => () => editor.Playback.ToStartCommand.Execute(null),
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

    /// <summary>An audio track's output dropdown (its properties) opening: list the devices again, in case one was plugged in.</summary>
    void OnOutputDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is Control { DataContext: TrackPropertiesViewModel properties }) properties.Row.RefreshDeviceList();
    }
}
