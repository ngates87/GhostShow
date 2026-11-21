namespace GhostShow.App.ViewModels;

/// <summary>
/// The browser's File menu. A device can't use the desktop's folder pickers, which are on this computer, so it works by
/// name inside one folder (the server's <see cref="GhostShow.Server.ShowFiles"/> does it). Each returns null when done,
/// otherwise why not.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<string> SavedShowsForDevice() => _files.SavedShows();

    public Task<string?> NewShowForDeviceAsync(bool discardUnsaved) => _files.NewForDeviceAsync(discardUnsaved);

    public Task<string?> OpenShowForDeviceAsync(string name, bool discardUnsaved) => _files.OpenByNameAsync(name, discardUnsaved);

    public Task<string?> SaveShowAsForDeviceAsync(string name) => _files.SaveAsByNameAsync(name);

    public Task<string?> RenameShowForDeviceAsync(string name) => _files.RenameAsync(name);
}
