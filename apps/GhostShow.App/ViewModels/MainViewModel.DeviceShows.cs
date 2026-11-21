namespace GhostShow.App.ViewModels;

/// <summary>
/// The browser's File menu. A device can't use the desktop's folder pickers, which are on this computer, so it works by
/// name inside one folder (the server's <see cref="GhostShow.Server.ShowFiles"/> does it). Each returns null when done,
/// otherwise why not.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<string> SavedShowsForDevice() => _files.SavedShows();

    public async Task<string?> NewShowForDeviceAsync(bool discardUnsaved)
    {
        if (await _files.NewForDeviceAsync(discardUnsaved) is { } problem) return problem;
        ShowEditor();
        return null;
    }

    public async Task<string?> OpenShowForDeviceAsync(string name, bool discardUnsaved)
    {
        if (await _files.OpenByNameAsync(name, discardUnsaved) is { } problem) return problem;
        ShowEditor();
        RefreshRecentShows();
        return null;
    }

    public Task<string?> SaveShowAsForDeviceAsync(string name) => _files.SaveAsByNameAsync(name);

    public Task<string?> RenameShowForDeviceAsync(string name) => _files.RenameAsync(name);
}
