namespace GhostShow.Core.Fixtures;

/// <summary>What the fixture library's screen shows about it.</summary>
/// <param name="Summary">How many fixture types it has ("1989 fixture profiles").</param>
/// <param name="DownloadStatus">e.g. "Open Fixture Library: 654 fixtures, downloaded 26 Oct 2026", or an invitation to download it.</param>
/// <param name="DownloadButtonText">"Download fixture library" or "Update fixture library".</param>
public sealed record FixtureLibraryStatus(string Summary, string DownloadStatus, string DownloadButtonText);

/// <summary>
/// The fixture library, as screens ask for it. It lives on the show computer (its files, its downloads, its custom
/// fixture types): <see cref="Storage.LocalFixtureLibrary"/> there, and in the browser, a stand-in that asks the show
/// computer over the network. Either way, the profiles themselves are in <c>ShowSession.Profiles</c>.
/// </summary>
public interface IFixtureLibrary
{
    FixtureLibraryStatus Status { get; }

    /// <summary><see cref="Status"/> changed.</summary>
    event Action? StatusChanged;

    /// <summary>The profiles changed (loaded, downloaded, a custom type saved or deleted), so screens rebuild their lists.</summary>
    event Action? Changed;

    /// <summary>Loads the library into the session. Takes a moment, and doesn't hold up the UI thread.</summary>
    Task LoadAsync();

    /// <summary>Downloads or updates the online library. If that fails, <see cref="Status"/> says why and the library is unchanged.</summary>
    Task DownloadAsync();

    /// <summary>Saves a custom fixture type and puts it in the library. Returns why it couldn't, or null.</summary>
    Task<string?> SaveCustomAsync(FixtureProfile profile);

    /// <summary>Deletes a custom fixture type and takes it out of the library. Returns why it couldn't, or null.</summary>
    Task<string?> DeleteCustomAsync(FixtureProfile profile);
}
