using System.Net.Http;
using System.Text.Json.Nodes;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.Browser.Remote;

/// <summary>
/// The fixture library in the browser: it's on the show computer (see libs/GhostShow.Web/RemoteServer.Library.cs).
/// Until the full library is needed, this copy of the show has the built-in profiles and the ones its fixtures use, which
/// the show computer sends with the show. <see cref="LoadAsync"/> fetches the rest, so the type picker works as on the
/// desktop. Custom fixture types are saved there and put in this copy once that worked.
///
/// Everything here runs on the UI thread.
/// </summary>
sealed class RemoteFixtureLibrary(ShowSession session, RemoteLink link, IReadOnlyList<FixtureProfile> builtIn) : IFixtureLibrary
{
    /// <summary>How many profiles to read before letting the page draw: reading them all at once would freeze it for a moment.</summary>
    const int ProfilesBetweenPauses = 50;

    static readonly HttpClient Http = new();

    bool _fetching;
    /// <summary>The version of the full library fetched; null until it has been.</summary>
    long? _fetched;
    /// <summary>The show computer's version, as it last said.</summary>
    long _latest;
    /// <summary>
    /// Our own saves and deletes whose "library" message hasn't come yet. That message comes around the same time as the
    /// answer, in either order, so these are counted until it does: this copy makes the same change, so it needn't fetch it.
    /// </summary>
    int _ownChanges;

    public FixtureLibraryStatus Status { get; private set; } = new("", "", "");

    public event Action? StatusChanged;

    public event Action? Changed;

    /// <summary>
    /// The profiles sent with the show or the patch. Before the full library has been fetched, they join the built-in
    /// ones; afterwards only one it doesn't have is added (a new custom type, until the library catches up).
    /// </summary>
    public void UseProfilesOfTheShow(JsonArray? sent)
    {
        var profiles = (sent ?? []).OfType<JsonNode>().Select(node => FixtureProfileLoader.ParseJson(node.ToJsonString()));
        if (_fetched is null)
        {
            var all = builtIn.ToDictionary(p => p.Id);
            foreach (var profile in profiles) all[profile.Id] = profile;
            session.UseProfiles(new FixtureProfileLibrary(all.Values));
            return;
        }
        foreach (var profile in profiles.Where(p => session.Profiles.Find(p.Id) is null).ToList())
            session.UseProfiles(session.Profiles.With(profile));
    }

    /// <summary>The whole show arrived, with these fields (a new connection): whatever we were waiting to hear of, we won't.</summary>
    public void OnHello(JsonObject message)
    {
        _ownChanges = 0;
        OnStatus(message);
    }

    /// <summary>A "library" message: what's on screen, and whether the profiles changed.</summary>
    public void OnStatus(JsonObject message)
    {
        Status = new FixtureLibraryStatus(
            message["summary"]?.GetValue<string>() ?? "",
            message["downloadStatus"]?.GetValue<string>() ?? "",
            message["downloadButtonText"]?.GetValue<string>() ?? "");
        StatusChanged?.Invoke();

        _latest = message["version"]?.GetValue<long>() ?? 0;
        if (_fetched is null || _fetched == _latest) return;
        if (_ownChanges > 0)
        {
            _ownChanges--;
            _fetched = _latest;
        }
        else _ = FetchAsync();
    }

    public Task LoadAsync() => _fetched is null ? FetchAsync() : Task.CompletedTask;

    async Task FetchAsync()
    {
        if (_fetching) return;
        _fetching = true;
        try
        {
            using var response = await Http.GetAsync($"{RemoteLink.Origin}/api/library?token={Uri.EscapeDataString(RemoteLink.Token ?? "")}");
            response.EnsureSuccessStatusCode();
            long version = long.Parse(response.Headers.GetValues("X-Library-Version").Single());
            string lines = await response.Content.ReadAsStringAsync();

            var profiles = new List<FixtureProfile>();
            foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                profiles.Add(FixtureProfileLoader.ParseJson(line));
                if (profiles.Count % ProfilesBetweenPauses == 0) await Task.Delay(1);
            }
            _fetched = version;
            session.UseProfiles(new FixtureProfileLibrary(profiles));
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or FormatException)
        {
            Status = Status with { Summary = $"Couldn't get the fixture library from the show computer: {e.Message}" };
            StatusChanged?.Invoke();
        }
        finally
        {
            _fetching = false;
        }
        if (_fetched is { } fetched && fetched != _latest && _latest != 0) await FetchAsync();   // it changed while we were reading it
    }

    /// <summary>Starts it on the show computer; "library" messages say how it's going, and it's fetched again when it's done.</summary>
    public async Task DownloadAsync()
    {
        if (await link.RequestAsync("downloadLibrary") is { } error)
        {
            Status = Status with { DownloadStatus = error };
            StatusChanged?.Invoke();
        }
    }

    public async Task<string?> SaveCustomAsync(FixtureProfile profile)
    {
        _ownChanges++;
        var error = await link.RequestAsync("saveProfile", ("profile", JsonNode.Parse(FixtureProfileLoader.ToJson(profile))));
        if (error is not null)
        {
            _ownChanges--;
            return error;
        }
        session.UseProfiles(session.Profiles.With(profile));
        Changed?.Invoke();
        return null;
    }

    public async Task<string?> DeleteCustomAsync(FixtureProfile profile)
    {
        _ownChanges++;
        var error = await link.RequestAsync("deleteProfile", ("profile", profile.Id));
        if (error is not null)
        {
            _ownChanges--;
            return error;
        }
        session.UseProfiles(session.Profiles.Without(profile.Id));
        Changed?.Invoke();
        return null;
    }
}
