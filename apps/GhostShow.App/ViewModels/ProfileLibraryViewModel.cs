using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>The fixture library on screen: its count, and downloading or updating the Open Fixture Library. The work is the library's (see <see cref="IFixtureLibrary"/>).</summary>
public sealed partial class ProfileLibraryViewModel : ObservableObject
{
    readonly IFixtureLibrary _library;

    public ProfileLibraryViewModel(IFixtureLibrary library)
    {
        _library = library;
        library.StatusChanged += () => OnPropertyChanged(string.Empty);
        library.Changed += () => Loaded?.Invoke();
    }

    /// <summary>Raised on the UI thread after the profiles change, so screens can rebuild their profile lists.</summary>
    public event Action? Loaded;

    public string Summary => _library.Status.Summary;

    /// <summary>e.g. "Open Fixture Library: 654 fixtures, downloaded 26 Oct 2026", or an invitation to download it.</summary>
    public string DownloadStatus => _library.Status.DownloadStatus;

    public string DownloadButtonText => _library.Status.DownloadButtonText;

    public Task LoadAsync() => _library.LoadAsync();

    [RelayCommand(AllowConcurrentExecutions = false)]
    Task Download() => _library.DownloadAsync();

    /// <summary>Saves a custom fixture type and puts it in the library straight away. Returns why it couldn't be saved, or null.</summary>
    public Task<string?> SaveCustomAsync(FixtureProfile profile) => _library.SaveCustomAsync(profile);

    /// <summary>Deletes a custom fixture type and takes it out of the library. Returns why it couldn't, or null.</summary>
    public Task<string?> DeleteCustomAsync(FixtureProfile profile) => _library.DeleteCustomAsync(profile);
}
