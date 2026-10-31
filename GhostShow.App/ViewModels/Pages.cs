namespace GhostShow.App.ViewModels;

/// <summary>Top-level tabs while a show is open, in the order MainWindow lists them.</summary>
public enum Section { Timeline, Lighting, Audio, Diagnostics }

/// <summary>
/// Every screen. Home is shown when no show is open. Lighting's screens (DMX setup to Monitor) are in their tab order.
/// </summary>
public enum Page { Home, Timeline, DmxSetup, Fixtures, Control, Channels, Monitor, Audio, Diagnostics }
