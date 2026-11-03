using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using Microsoft.Extensions.Logging;

namespace GhostShow.Core.Storage;

/// <summary>
/// The fixture library on the show computer: loads it (off the UI thread), downloads or updates the Open Fixture
/// Library on request (nothing is downloaded unless asked), and keeps custom fixture types as files.
/// </summary>
public sealed class LocalFixtureLibrary : IFixtureLibrary
{
    static readonly HttpClient Http = CreateHttpClient();

    readonly ShowSession _session;
    readonly AppPaths _paths;
    readonly MachineSettings _settings;
    readonly ILogger _logger;
    string _summary = "Loading fixture profiles…";
    string _downloadStatus = "";
    string _downloadButtonText = "";
    bool _downloading;

    public LocalFixtureLibrary(ShowSession session, AppPaths paths, MachineSettings settings, ILogger logger)
    {
        _session = session;
        _paths = paths;
        _settings = settings;
        _logger = logger;
        DescribeDownload();
    }

    public FixtureLibraryStatus Status => new(_summary, _downloadStatus, _downloadButtonText);

    public event Action? StatusChanged;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        var library = await Task.Run(() => FixtureProfileLoader.Load(_paths, _settings));
        _session.UseProfiles(library);
        _summary = $"{library.Profiles.Count} fixture profiles" +
                   (library.LoadErrors.Count > 0 ? $"; {library.LoadErrors.Count} files couldn't be read (see Diagnostics)" : "");
        foreach (var error in library.LoadErrors.Take(20)) _logger.LogWarning("Fixture profile {Error}", error);
        _logger.LogInformation("Loaded {Count} fixture profiles.", library.Profiles.Count);
        StatusChanged?.Invoke();
        Changed?.Invoke();
    }

    public async Task DownloadAsync()
    {
        if (_downloading) return;   // the desktop and a device can both ask
        _downloading = true;
        _downloadStatus = $"Downloading the {OnlineFixtureLibrary.SourceName}…";
        StatusChanged?.Invoke();
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
            _downloadStatus = $"Download failed: {ex.Message} The previous library (if any) is unchanged.";
        }
        finally
        {
            _downloading = false;
        }
        StatusChanged?.Invoke();
    }

    public Task<string?> SaveCustomAsync(FixtureProfile profile)
    {
        try
        {
            CustomProfiles.Save(_paths, profile);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Couldn't save the custom fixture type {Profile}: {Error}", profile, ex.Message);
            return Task.FromResult<string?>($"Couldn't save it: {ex.Message}");
        }
        _logger.LogInformation("Saved the custom fixture type {Profile} ({Id}).", profile, profile.Id);
        _session.UseProfiles(_session.Profiles.With(profile));
        Changed?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> DeleteCustomAsync(FixtureProfile profile)
    {
        try
        {
            CustomProfiles.Delete(_paths, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>($"Couldn't delete it: {ex.Message}");
        }
        _logger.LogInformation("Deleted the custom fixture type {Profile} ({Id}).", profile, profile.Id);
        _session.UseProfiles(_session.Profiles.Without(profile.Id));
        Changed?.Invoke();
        return Task.FromResult<string?>(null);
    }

    void DescribeDownload()
    {
        var installed = OnlineFixtureLibrary.Installed(_paths);
        _downloadStatus = installed is null
            ? $"The {OnlineFixtureLibrary.SourceName} (free, ~650 fixtures, about 3 MB) isn't downloaded yet."
            : $"{installed.Source}: {installed.FixtureCount} fixtures, downloaded {installed.DownloadedAt:d MMM yyyy}.";
        _downloadButtonText = installed is null ? "Download fixture library" : "Update fixture library";
    }

    static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GhostShow (+https://github.com/)");
        return http;
    }
}
