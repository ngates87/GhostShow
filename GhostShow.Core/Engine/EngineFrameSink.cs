using GhostShow.Core.Output;

namespace GhostShow.Core.Engine;

/// <summary>
/// Lets the prototype timeline player (<see cref="SceneRunner"/>, frozen for Phase 1) drive the engine instead of
/// hardware: each frame it renders replaces the universe's values, and the normal output services send them.
/// Frames for universes the show doesn't define are dropped.
/// </summary>
public sealed class EngineFrameSink(ShowEngine engine) : IDmxOutput
{
    public void Send(int universe, ReadOnlySpan<byte> data) => engine.ReplaceUniverse(universe, data);

    public void Dispose() { }
}
