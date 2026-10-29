using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>A folder or a timeline in the tree. Renaming happens in place: <see cref="IsEditing"/> shows a text box.</summary>
public abstract partial class TimelineTreeNode : ObservableObject
{
    public abstract string Name { get; }

    /// <summary>The folder this node is in ("" at the top level).</summary>
    public abstract string ParentFolder { get; }

    public abstract bool IsFolder { get; }

    /// <summary>Shown in the tree: the name, with " *" for a timeline with unsaved changes.</summary>
    public virtual string Title => Name;

    public ObservableCollection<TimelineTreeNode> Children { get; } = new();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The text being typed while renaming.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

/// <summary>A folder the selected item can be moved to (the context menu's Move to).</summary>
public sealed record MoveTarget(string Label, string Folder, System.Windows.Input.ICommand Move);

public sealed class TimelineFolderNode(string path) : TimelineTreeNode
{
    public string Path { get; } = path;
    public override string Name => TimelineNames.LeafOf(Path);
    public override string ParentFolder => TimelineNames.ParentOf(Path);
    public override bool IsFolder => true;
}

public sealed class TimelineNode(ShowTimeline timeline) : TimelineTreeNode
{
    public ShowTimeline Timeline { get; } = timeline;
    public override string Name => Timeline.Name;
    public override string ParentFolder => Timeline.Folder;
    public override bool IsFolder => false;
    public override string Title => Timeline.HasUnsavedChanges ? $"{Timeline.Name} *" : Timeline.Name;
}

/// <summary>
/// The show's timelines as a tree of folders, mirroring the show folder's timelines/ directory: creating, renaming,
/// moving and deleting here changes the show, and the files follow when it is saved (see ShowFolderStore). These
/// are show-structure changes, not timeline edits: they're on the show's undo history (Undo takes back whichever is
/// latest, these or the open timeline's edits); deleting asks first. The tree follows every change to the show's
/// structure, including undo and edits from other devices.
/// </summary>
public sealed partial class TimelineTreeViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly Action _markDirty;
    readonly Action<ShowTimeline> _open;
    bool _editing;

    /// <param name="open">Opens a timeline in the editor (a tab).</param>
    public TimelineTreeViewModel(ShowSession session, Action markDirty, Action<ShowTimeline> open)
    {
        _session = session;
        _markDirty = markDirty;
        _open = open;
        session.Document.Changed += change =>
        {
            if (!_editing && change.Ops.Any(o => IsStructural(o.Path))) Changed();
        };
    }

    Project Project => _session.Project;

    /// <summary>A tree change through the show's document (one undo step); the command rebuilds the tree itself.</summary>
    void Edit(string description, Action change, params object[] roots)
    {
        _editing = true;
        try
        {
            _session.Document.Record(description, change, roots);
        }
        finally
        {
            _editing = false;
        }
    }

    static object Only(object o) => ShowDocument.Shallow(o);

    /// <summary>Whether an edit at this path changes the tree: the timelines or folders, or a timeline's name or folder.</summary>
    static bool IsStructural(string path)
    {
        if (path is "timelines" or "timelineFolders") return true;
        var parts = path.Split('/');
        return parts[0] == "timelines" && (parts.Length == 2 || (parts.Length == 3 && parts[2] is "name" or "folder"));
    }

    public ObservableCollection<TimelineTreeNode> Roots { get; } = new();

    /// <summary>After a rename, move, new or delete: names and paths shown elsewhere (tabs, transport) may have changed.</summary>
    public event Action? StructureChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial TimelineTreeNode? SelectedNode { get; set; }

    public bool HasSelection => SelectedNode is not null;

    /// <summary>Why the last tree operation was refused (a name in use, an invalid name, ...); null when it worked.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Where new items go: the selected folder, or the folder of the selected timeline.</summary>
    string TargetFolder => SelectedNode switch
    {
        TimelineFolderNode folder => folder.Path,
        { } node => node.ParentFolder,
        _ => "",
    };

    /// <summary>
    /// Rebuilds the tree from the show, keeping which folders were open and what was selected. Folders first, then
    /// timelines, by name. After renaming or moving a folder, pass its new path so it stays selected (and open).
    /// </summary>
    public void Rebuild(string? movedFolderFrom = null, string? movedFolderTo = null)
    {
        string Follow(string path) => movedFolderFrom is not null && TimelineNames.IsWithin(path, movedFolderFrom)
            ? movedFolderTo + path[movedFolderFrom.Length..] : path;
        var expanded = AllNodes().OfType<TimelineFolderNode>().Where(f => f.IsExpanded).Select(f => Follow(f.Path)).ToHashSet(TimelineNames.Comparer);
        var selected = SelectedNode switch { TimelineFolderNode f => Follow(f.Path), TimelineNode t => t.Timeline.Id.ToString(), _ => null };

        var folders = Project.TimelineFolders.ToDictionary(f => f, f => new TimelineFolderNode(f) { IsExpanded = expanded.Contains(f) }, TimelineNames.Comparer);
        ObservableCollection<TimelineTreeNode> ChildrenOf(string folder) => folder.Length == 0 ? Roots : folders[folder].Children;

        Roots.Clear();
        foreach (var folder in folders.Values.OrderBy(f => f.Name, TimelineNames.Comparer))
            if (folders.ContainsKey(folder.ParentFolder) || folder.ParentFolder.Length == 0) ChildrenOf(folder.ParentFolder).Add(folder);
        foreach (var timeline in Project.Timelines.OrderBy(t => t.Name, TimelineNames.Comparer))
            ChildrenOf(folders.ContainsKey(timeline.Folder) ? timeline.Folder : "").Add(new TimelineNode(timeline));

        SelectedNode = AllNodes().FirstOrDefault(n => n switch
        {
            TimelineFolderNode f => TimelineNames.Comparer.Equals(f.Path, selected),
            TimelineNode t => t.Timeline.Id.ToString() == selected,
            _ => false,
        });
    }

    /// <summary>After an edit or a save: the " *" marks.</summary>
    public void RefreshTitles()
    {
        foreach (var node in AllNodes()) node.Refresh();
    }

    public IEnumerable<TimelineTreeNode> AllNodes()
    {
        var stack = new Stack<TimelineTreeNode>(Roots);
        while (stack.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    public TimelineNode? NodeOf(ShowTimeline timeline) => AllNodes().OfType<TimelineNode>().FirstOrDefault(n => n.Timeline == timeline);

    /// <summary>Selects a timeline's node, opening the folders above it.</summary>
    public void Reveal(ShowTimeline timeline)
    {
        for (var f = timeline.Folder; f.Length > 0; f = TimelineNames.ParentOf(f))
            if (AllNodes().OfType<TimelineFolderNode>().FirstOrDefault(n => n.Path == f) is { } folder) folder.IsExpanded = true;
        SelectedNode = NodeOf(timeline);
    }

    // ---- commands ----

    [RelayCommand]
    void NewTimeline()
    {
        string folder = TargetFolder;
        var timeline = Project.NewTimeline(TimelineNames.Unique("New timeline", NamesIn(folder)));
        timeline.Folder = folder;
        Edit("New timeline", () => Project.Timelines.Add(timeline), Only(Project));
        Changed();
        Reveal(timeline);
        _open(timeline);
        StartRename();
    }

    [RelayCommand]
    void NewFolder()
    {
        string parent = TargetFolder;
        string path = TimelineNames.Combine(parent, TimelineNames.Unique("New folder", FolderNamesIn(parent)));
        Edit("New folder", () => Project.AddFolder(path), Only(Project));
        Changed();
        SelectedNode = AllNodes().OfType<TimelineFolderNode>().First(f => f.Path == path);
        StartRename();
    }

    [RelayCommand]
    void Open()
    {
        if (SelectedNode is TimelineNode node) _open(node.Timeline);
        else if (SelectedNode is { } folder) folder.IsExpanded = !folder.IsExpanded;
    }

    /// <summary>Shows the in-place text box on the selected node (F2, or the context menu).</summary>
    [RelayCommand]
    void StartRename()
    {
        if (SelectedNode is not { } node) return;
        node.EditName = node.Name;
        node.IsEditing = true;
    }

    /// <summary>Enter or leaving the box. A refused name keeps the old one and says why.</summary>
    public bool CommitRename(TimelineTreeNode node)
    {
        if (!node.IsEditing) return true;
        node.IsEditing = false;
        string name = node.EditName.Trim();
        return name == node.Name || Rename(node, name);
    }

    public void CancelRename(TimelineTreeNode node) => node.IsEditing = false;

    public bool Rename(TimelineTreeNode node, string name)
    {
        if (Refuse(TimelineNames.Problem(name))) return false;
        switch (node)
        {
            case TimelineNode t:
                bool taken = Project.Timelines.Any(o => o != t.Timeline && TimelineNames.Comparer.Equals(o.Folder, t.Timeline.Folder)
                                                        && TimelineNames.Comparer.Equals(o.Name, name));
                if (Refuse(taken ? $"There's already a timeline called '{name}' here." : null)) return false;
                Edit("Rename timeline", () => t.Timeline.Name = name, Only(t.Timeline));   // its file is renamed on save
                break;
            case TimelineFolderNode f:
                string path = TimelineNames.Combine(f.ParentFolder, name);
                if (Refuse(!TimelineNames.Comparer.Equals(path, f.Path) && Project.TimelineFolders.Contains(path)
                    ? $"There's already a folder called '{name}' here." : null)) return false;
                MoveFolder("Rename folder", f.Path, path);
                Changed(f.Path, path);
                return true;
        }
        Changed();
        return true;
    }

    [RelayCommand]
    void Duplicate()
    {
        if (SelectedNode is not TimelineNode node) return;
        var copy = node.Timeline.Clone(TimelineNames.Unique($"{node.Timeline.Name} copy", NamesIn(node.Timeline.Folder)));
        Edit("Duplicate timeline", () => Project.Timelines.Add(copy), Only(Project));
        Changed();
        Reveal(copy);
        _open(copy);
    }

    /// <summary>Delete key or the context menu: shows the confirmation.</summary>
    [RelayCommand]
    void AskDelete() => IsConfirmingDelete = SelectedNode is not null;

    [RelayCommand]
    void CancelDelete() => IsConfirmingDelete = false;

    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    /// <summary>Deletes the selected timeline, or a folder with everything in it, once confirmed (Undo brings it back).</summary>
    [RelayCommand]
    void Delete()
    {
        IsConfirmingDelete = false;
        switch (SelectedNode)
        {
            case TimelineNode node:
                Edit($"Delete timeline '{node.Timeline.Name}'", () => Project.Timelines.Remove(node.Timeline), Only(Project));
                break;
            case TimelineFolderNode folder:
                Edit($"Delete folder '{folder.Name}'", () =>
                {
                    Project.Timelines.RemoveAll(t => TimelineNames.IsWithin(t.Folder, folder.Path));
                    Project.TimelineFolders.RemoveWhere(f => TimelineNames.IsWithin(f, folder.Path));
                }, Only(Project));
                break;
            default:
                return;
        }
        SelectedNode = null;
        Changed();
    }

    /// <summary>What the delete confirmation says.</summary>
    public string DeleteQuestion => SelectedNode switch
    {
        TimelineNode t => $"Delete the timeline '{t.Name}'? Its file is removed when the show is saved. Undo (Ctrl+Z) brings it back.",
        TimelineFolderNode f => $"Delete the folder '{f.Name}' and the {Project.Timelines.Count(t => TimelineNames.IsWithin(t.Folder, f.Path))} timelines in it? Their files are removed when the show is saved. Undo (Ctrl+Z) brings them back.",
        _ => "",
    };

    partial void OnSelectedNodeChanged(TimelineTreeNode? value)
    {
        IsConfirmingDelete = false;
        OnPropertyChanged(nameof(DeleteQuestion));
        OnPropertyChanged(nameof(MoveTargets));
    }

    /// <summary>Where the selected item can go: every other folder (not itself or inside itself), and the top level.</summary>
    public IReadOnlyList<MoveTarget> MoveTargets => SelectedNode is not { } node ? [] :
        new[] { "" }.Concat(Project.TimelineFolders)
            .Where(f => !TimelineNames.Comparer.Equals(f, node.ParentFolder))
            .Where(f => node is not TimelineFolderNode folder || !TimelineNames.IsWithin(f, folder.Path))
            .Select(f => new MoveTarget(f.Length == 0 ? "(top level)" : f, f, MoveSelectedToCommand))
            .ToList();

    [RelayCommand]
    void MoveSelectedTo(MoveTarget? target)
    {
        if (SelectedNode is { } node && target is not null) Move(node, target.Folder);
    }

    /// <summary>Drag and drop: moves a timeline or folder into a folder ("" = top level). False, with a message, if it can't go there.</summary>
    public bool Move(TimelineTreeNode node, string targetFolder)
    {
        if (targetFolder.Length > 0 && !Project.TimelineFolders.Contains(targetFolder)) return false;
        switch (node)
        {
            case TimelineNode t:
                if (TimelineNames.Comparer.Equals(t.Timeline.Folder, targetFolder)) return false;
                if (Refuse(NamesIn(targetFolder).Contains(t.Timeline.Name, TimelineNames.Comparer)
                    ? $"There's already a timeline called '{t.Timeline.Name}' in {Describe(targetFolder)}." : null)) return false;
                Edit("Move timeline", () => t.Timeline.Folder = targetFolder, Only(t.Timeline));
                break;
            case TimelineFolderNode f:
                if (TimelineNames.Comparer.Equals(f.ParentFolder, targetFolder)) return false;
                if (Refuse(TimelineNames.IsWithin(targetFolder, f.Path) ? "A folder can't go inside itself." : null)) return false;
                string path = TimelineNames.Combine(targetFolder, f.Name);
                if (Refuse(Project.TimelineFolders.Contains(path) ? $"There's already a folder called '{f.Name}' in {Describe(targetFolder)}." : null)) return false;
                MoveFolder("Move folder", f.Path, path);
                Changed(f.Path, path);
                return true;
        }
        Changed();
        if (node is TimelineNode moved) Reveal(moved.Timeline);
        return true;
    }

    /// <summary>Renames or moves a folder (one undo step): every folder and timeline inside follows.</summary>
    void MoveFolder(string description, string from, string to)
    {
        string Rebase(string path) => to + path[from.Length..];
        var moving = Project.Timelines.Where(t => TimelineNames.IsWithin(t.Folder, from)).ToList();
        Edit(description, () =>
        {
            var inside = Project.TimelineFolders.Where(f => TimelineNames.IsWithin(f, from)).ToList();
            Project.TimelineFolders.RemoveWhere(f => TimelineNames.IsWithin(f, from));
            Project.AddFolder(to);
            foreach (var f in inside) Project.TimelineFolders.Add(Rebase(f));
            foreach (var t in moving) t.Folder = Rebase(t.Folder);
        }, [Only(Project), .. moving.Select(Only)]);
    }

    void Changed(string? movedFolderFrom = null, string? movedFolderTo = null)
    {
        Message = null;
        _markDirty();
        Rebuild(movedFolderFrom, movedFolderTo);
        StructureChanged?.Invoke();
    }

    bool Refuse(string? problem)
    {
        Message = problem;
        return problem is not null;
    }

    IEnumerable<string> NamesIn(string folder) =>
        Project.Timelines.Where(t => TimelineNames.Comparer.Equals(t.Folder, folder)).Select(t => t.Name);

    IEnumerable<string> FolderNamesIn(string parent) =>
        Project.TimelineFolders.Where(f => TimelineNames.Comparer.Equals(TimelineNames.ParentOf(f), parent)).Select(TimelineNames.LeafOf);

    static string Describe(string folder) => folder.Length == 0 ? "the top level" : $"'{folder}'";
}
