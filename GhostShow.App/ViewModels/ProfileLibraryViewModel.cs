using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Loads the fixture profile library (off the UI thread) and downloads or updates the Open Fixture Library on request.
/// Nothing is downloaded unless the user presses the button.
/// </summary>
public sealed partial class ProfileLibraryViewModel : ObservableObject
{
    static readonly HttpClient Http = CreateHttpClient();

    readonly ShowSession _session;
    readonly AppPaths _paths;
    readonly MachineSettings _settings;
    readonly ILogger _logger;

    public ProfileLibraryViewModel(ShowSession session, AppPaths paths, MachineSettings settings, ILogger logger)
    {
        _session = session;
        _paths = paths;
        _settings = settings;
        _logger = logger;
        DescribeDownload();
    }

    /// <summary>Raised on the UI thread after a (re)load, so screens can rebuild their profile lists.</summary>
    public event Action? Loaded;

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Loading fixture profiles…";

    /// <summary>e.g. "Open Fixture Library: 654 fixtures, downloaded 26 Oct 2026", or an invitation to download it.</summary>
    [ObservableProperty]
    public partial string DownloadStatus { get; private set; } = "";

    [ObservableProperty]
    public partial string DownloadButtonText { get; private set; } = "";

    public async Task LoadAsync()
    {
        var library = await Task.Run(() => FixtureProfileLoader.Load(_paths, _settings));
        _session.UseProfiles(library);
        Summary = $"{library.Profiles.Count} fixture profiles" +
                  (library.LoadErrors.Count > 0 ? $"; {library.LoadErrors.Count} files couldn't be read (see Diagnostics)" : "");
        foreach (var error in library.LoadErrors.Take(20)) _logger.LogWarning("Fixture profile {Error}", error);
        _logger.LogInformation("Loaded {Count} fixture profiles.", library.Profiles.Count);
        Loaded?.Invoke();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Download()
    {
        DownloadStatus = $"Downloading the {OnlineFixtureLibrary.SourceName}…";
        try
        {
            var info = await Task.Run(() => OnlineFixtureLibrary.DownloadAsync(Http, _paths));
            _logger.LogInformation("Downloaded the {Source}: {Count} fixtures.", info.Source, info.FixtureCount);
            await LoadAsync();
            DescribeDownload();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
        {
            _logger.LogWarning("Fixture library download failed: {Error}", ex.Message);
            DownloadStatus = $"Download failed: {ex.Message} The previous library (if any) is unchanged.";
        }
    }

    void DescribeDownload()
    {
        var installed = OnlineFixtureLibrary.Installed(_paths);
        DownloadStatus = installed is null
            ? $"The {OnlineFixtureLibrary.SourceName} (free, ~650 fixtures, about 3 MB) isn't downloaded yet."
            : $"{installed.Source}: {installed.FixtureCount} fixtures, downloaded {installed.DownloadedAt:d MMM yyyy}.";
        DownloadButtonText = installed is null ? "Download fixture library" : "Update fixture library";
    }

    static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GhostShow (+https://github.com/)");
        return http;
    }
}
