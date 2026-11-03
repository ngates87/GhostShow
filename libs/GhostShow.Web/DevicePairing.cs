using System.Security.Cryptography;
using System.Text;
using GhostShow.Core.Storage;

namespace GhostShow.Web;

/// <summary>
/// Pairing devices with this computer: the PIN shown on the desktop, the tokens paired devices keep, and the list of
/// them (in <see cref="RemoteSettings.Devices"/>, saved with the machine settings). Thread-safe: pairing requests come
/// from the server's threads, revoking from the UI's.
/// </summary>
public sealed class DevicePairing
{
    /// <summary>Wrong PINs allowed in a row before pairing pauses (a 6-digit PIN can't be guessed in a few tries).</summary>
    const int MaxFailures = 5;
    static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    readonly RemoteSettings _settings;
    readonly TimeProvider _clock;
    readonly Lock _lock = new();
    int _failures;
    DateTimeOffset _lockedUntil;

    public DevicePairing(RemoteSettings settings, TimeProvider? clock = null)
    {
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        Pin = NewPinValue();
    }

    /// <summary>The 6-digit PIN a device enters to pair (shown on the desktop). New each time the app starts.</summary>
    public string Pin { get; private set; }

    /// <summary>A device paired, was removed, or changed role.</summary>
    public event Action? Changed;

    public void NewPin()
    {
        lock (_lock) Pin = NewPinValue();
        Changed?.Invoke();
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_lock) return [.. _settings.Devices]; }
    }

    /// <summary>The outcome of a pairing attempt: the new device and its token, or why not.</summary>
    public sealed record Result(PairedDevice? Device, string? Token, string? Problem, bool TooManyTries = false);

    public Result Pair(string pin, string name, RemoteRole role)
    {
        PairedDevice device;
        string token;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (now < _lockedUntil) return new(null, null, "Too many wrong PINs: wait a minute and try again.", TooManyTries: true);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin.Trim()), Encoding.UTF8.GetBytes(Pin)))
            {
                if (++_failures >= MaxFailures)
                {
                    _failures = 0;
                    _lockedUntil = now + Lockout;
                }
                return new(null, null, "That isn't the PIN shown on the show computer.");
            }
            _failures = 0;
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            name = name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 60)] : "Device";
            device = new PairedDevice { Name = name, Role = role, TokenHash = Hash(token), Paired = now, LastSeen = now };
            _settings.Devices.Add(device);
        }
        Changed?.Invoke();
        return new(device, token, null);
    }

    /// <summary>The paired device a token belongs to, or null (never paired, or removed).</summary>
    public PairedDevice? Find(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        lock (_lock) return _settings.Devices.FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), Encoding.ASCII.GetBytes(hash)));
    }

    public void Seen(PairedDevice device)
    {
        lock (_lock) device.LastSeen = _clock.GetUtcNow();
        Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_lock) removed = _settings.Devices.RemoveAll(d => d.Id == id) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void SetRole(Guid id, RemoteRole role)
    {
        lock (_lock)
        {
            if (_settings.Devices.FirstOrDefault(d => d.Id == id) is not { } device || device.Role == role) return;
            device.Role = role;
        }
        Changed?.Invoke();
    }

    static string NewPinValue() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
