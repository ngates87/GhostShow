namespace GhostShow.Core.Engine;

/// <summary>
/// Outcome of a show command. Bad input from a user, an OSC message or a web request is an ordinary event,
/// not an exception, so commands report it here. A struct, because slider moves issue commands at a high rate.
/// </summary>
public readonly record struct CommandResult(bool Succeeded, string? Error)
{
    public static CommandResult Ok => new(true, null);

    public static CommandResult Fail(string error) => new(false, error);
}
