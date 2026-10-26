namespace GhostShow.Core.Output;

public interface IDmxOutput : IDisposable
{
    /// <summary>Send one full frame (up to 512 channels) for a universe.</summary>
    void Send(int universe, ReadOnlySpan<byte> data);
}

public interface IAudioPlayer : IDisposable
{
    void Play(string file, int volume);
    void StopAll();
}
