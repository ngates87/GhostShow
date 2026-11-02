namespace GhostShow.Core.Storage;

/// <summary>Where GhostShow keeps per-machine files. Overridable so tests and portable installs can use other folders.</summary>
public sealed record AppPaths(string ConfigDirectory)
{
    /// <summary>Linux: ~/.config/GhostShow. Windows: %APPDATA%\GhostShow. macOS: ~/Library/Application Support/GhostShow.</summary>
    public static AppPaths Default { get; } =
        new(Path.Combine(FolderPath(Environment.SpecialFolder.ApplicationData), "GhostShow"));

    // Without DoNotVerify, GetFolderPath returns "" when the folder doesn't exist yet (e.g. a fresh ~/.config),
    // which would silently turn every path relative to the working directory.
    static string FolderPath(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    public string MachineSettingsFile => Path.Combine(ConfigDirectory, "machine.json");

    /// <summary>The user's own fixture profiles (.json or .qxf). Profiles here replace built-in ones with the same ID.</summary>
    public string UserProfilesDirectory => Path.Combine(ConfigDirectory, "profiles");

    /// <summary>Where QLC+ installs its fixture library on each OS, plus QLC+'s per-user folder. Missing ones are skipped.</summary>
    public static IReadOnlyList<string> QlcPlusFixtureDirectories
    {
        get
        {
            string home = FolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
                return [@"C:\QLC+\Fixtures", Path.Combine(home, "QLC+", "Fixtures")];
            if (OperatingSystem.IsMacOS())
                return ["/Applications/QLC+.app/Contents/Resources/Fixtures", Path.Combine(home, "Library", "Application Support", "QLC+", "Fixtures")];
            return ["/usr/share/qlcplus/fixtures", Path.Combine(home, ".qlcplus", "fixtures")];
        }
    }
}
