using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GhostShow.Browser.Remote;

namespace GhostShow.Browser;

/// <summary>
/// The browser's File menu: New, Open, Save and Rename. The show computer does the work and picks the place (see
/// MainViewModel.DeviceShows.cs); this only asks the questions. Its questions appear in <see cref="Questions"/>, laid
/// over the page, because a browser page can't open a second window.
/// </summary>
sealed class FileMenu
{
    readonly RemoteLink _link;
    readonly Action<string> _notice;
    readonly Func<string> _showName;
    readonly MenuItem _rename = new() { Header = "Rename…" };
    bool _unsaved;
    bool _needsFirstSave;

    public Button Button { get; } = new() { Content = "File", Padding = new(12, 3), Margin = new(4, 3), VerticalAlignment = VerticalAlignment.Center };
    public Border Questions { get; } = new() { IsVisible = false, Background = new SolidColorBrush(Colors.Black, 0.6) };

    public FileMenu(RemoteLink link, Action<string> notice, Func<string> showName)
    {
        _link = link;
        _notice = notice;
        _showName = showName;

        var newShow = new MenuItem { Header = "New" };
        var open = new MenuItem { Header = "Open…" };
        var save = new MenuItem { Header = "Save" };
        newShow.Click += (_, _) => New();
        open.Click += (_, _) => _link.Send("listShows");
        save.Click += (_, _) => Save();
        _rename.Click += (_, _) => AskForName("Rename show", "Rename", _showName(), name => RequestAsync("renameShow", name));
        var menu = new MenuFlyout();
        foreach (var item in new[] { newShow, open, save, _rename }) menu.Items.Add(item);
        Button.Flyout = menu;

        _link.Shows += message => Dispatcher.UIThread.Post(() => ShowList(message));
    }

    /// <summary>From each status: decides what Save does, and whether leaving the show needs a warning.</summary>
    public void Update(bool unsaved, bool needsFirstSave)
    {
        _unsaved = unsaved;
        _needsFirstSave = needsFirstSave;
        _rename.IsEnabled = !needsFirstSave;
    }

    /// <summary>File → Save and Ctrl+S: a show without a name is asked for one, and saves itself from then on.</summary>
    public void Save()
    {
        if (_needsFirstSave)
            AskForName("Save show", "Save", _showName(), name => RequestAsync("saveShowAs", name));
        else
            _ = SaveNowAsync();
    }

    async Task SaveNowAsync()
    {
        if (await _link.RequestAsync("save") is { } error) _notice(error);
    }

    void New() => IfLosingWorkIsAgreed("starting a new show", discard => _ = LeaveAsync("newShow", discard, name: null));

    void ShowList(JsonObject message)
    {
        var names = message["shows"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(Heading("Open show"));
        if (names.Count == 0) content.Children.Add(new TextBlock { Text = "No shows have been saved here yet.", Foreground = Brushes.Gray });
        foreach (string name in names)
        {
            var row = new Button { Content = name, HorizontalAlignment = HorizontalAlignment.Stretch };
            row.Click += (_, _) => IfLosingWorkIsAgreed($"opening {name}", discard => _ = LeaveAsync("openShow", discard, name));
            content.Children.Add(row);
        }
        var list = new ScrollViewer { MaxHeight = 360, Content = content };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close();
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(list);
        panel.Children.Add(cancel);
        Show(panel);
    }

    /// <summary>Only a never-saved show can be lost: the show computer saves any other before leaving it.</summary>
    void IfLosingWorkIsAgreed(string doing, Action<bool> proceed)
    {
        if (!_unsaved || !_needsFirstSave)
        {
            Close();
            proceed(false);
            return;
        }
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Heading("Lose this show?"));
        panel.Children.Add(new TextBlock { Text = $"This show has never been saved. If you carry on with {doing}, it will be lost.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" };
        var discard = new Button { Content = "Don't save" };
        var save = new Button { Content = "Save it first…" };
        cancel.Click += (_, _) => Close();
        discard.Click += (_, _) => { Close(); proceed(true); };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(discard);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Show(panel);
    }

    async Task LeaveAsync(string action, bool discard, string? name)
    {
        string? error = name is null
            ? await _link.RequestAsync(action, ("discardUnsaved", discard))
            : await _link.RequestAsync(action, ("name", name), ("discardUnsaved", discard));
        if (error is not null) _notice(error);
    }

    Task<string?> RequestAsync(string action, string name) => _link.RequestAsync(action, ("name", name));

    /// <summary>Stays open until the show computer accepts the name, so a refused one can be corrected.</summary>
    void AskForName(string title, string confirmLabel, string suggested, Func<string, Task<string?>> submit)
    {
        var box = new TextBox { Text = suggested, MinWidth = 320 };
        var problem = new TextBlock { Foreground = Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        async void Submit()
        {
            confirm.IsEnabled = false;
            string? error = await submit((box.Text ?? "").Trim());
            confirm.IsEnabled = true;
            if (error is null)
            {
                Close();
                return;
            }
            problem.Text = error;
            problem.IsVisible = true;
        }
        confirm.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Heading(title));
        panel.Children.Add(box);
        panel.Children.Add(problem);
        panel.Children.Add(new TextBlock { Text = "It's kept in Documents/GhostShow on the show computer.", Foreground = Brushes.Gray, FontSize = 12 });
        panel.Children.Add(buttons);
        Show(panel);
        box.SelectAll();
        Dispatcher.UIThread.Post(() => box.Focus());
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold };

    void Show(Control content)
    {
        Questions.Child = new Border
        {
            Child = content,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            CornerRadius = new(6),
            Padding = new(16),
            MinWidth = 300,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Questions.IsVisible = true;
    }

    void Close()
    {
        Questions.IsVisible = false;
        Questions.Child = null;
    }
}
