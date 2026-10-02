using Microsoft.Xna.Framework.Audio;
using NVorbis;

namespace XPilot.Desktop.Audio;

/// <summary>
/// Plays the .ogg files in the "music" folder next to the executable in random order, never the same track
/// twice in a row. Tracks are decoded a little at a time and streamed, so they don't sit in memory whole.
/// Call <see cref="Update"/> every frame. With no music folder or no audio device it simply stays quiet.
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    /// <summary>Each buffer holds this much audio; a few are kept queued ahead.</summary>
    private const float BufferSeconds = 0.1f;
    private const int BuffersAhead = 4;

    private readonly List<string> _tracks;
    private readonly List<string> _queue = [];
    private readonly Random _rng = new();
    private VorbisReader? _reader;
    private DynamicSoundEffectInstance? _instance;
    private float[] _samples = [];
    private byte[] _bytes = [];
    private string? _currentPath, _lastPlayed;
    private bool _broken;
    private float _volume;

    private MusicPlayer(List<string> tracks) => _tracks = tracks;

    public static MusicPlayer Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "music");
        var tracks = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ogg").Order().ToList() : [];
        return new MusicPlayer(tracks);
    }

    public int TrackCount => _tracks.Count;
    /// <summary>A readable name for the track playing, or null.</summary>
    public string? NowPlaying { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_instance != null) _instance.Volume = _volume;
        }
    }

    public void Update()
    {
        if (_broken || _tracks.Count == 0) return;
        try
        {
            if (_reader == null) StartNext();
            if (_reader == null || _instance == null) return;

            while (_instance.PendingBufferCount < BuffersAhead && !_reader.IsEndOfStream) SubmitBuffer();
            if (_instance.State != SoundState.Playing) _instance.Play();

            // Finished decoding and everything queued has played: on to the next track.
            if (_reader.IsEndOfStream && _instance.PendingBufferCount == 0) StopTrack();
        }
        catch (NoAudioHardwareException)
        {
            _broken = true;
            StopTrack();
        }
    }

    /// <summary>Moves straight on to another random track.</summary>
    public void Skip()
    {
        StopTrack();
        Update();
    }

    private void StartNext()
    {
        // Try each track at most once, so a folder of unreadable files can't spin forever.
        for (int attempt = 0; attempt < _tracks.Count && _reader == null; attempt++)
        {
            if (_queue.Count == 0) RefillQueue();
            var path = _queue[0];
            _queue.RemoveAt(0);
            try
            {
                var reader = new VorbisReader(path);
                if (reader.Channels is < 1 or > 2) throw new InvalidDataException($"{reader.Channels} channels");
                _reader = reader;
                _instance = new DynamicSoundEffectInstance(reader.SampleRate, reader.Channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono)
                {
                    Volume = _volume,
                };
                int count = (int)(reader.SampleRate * BufferSeconds) * reader.Channels;
                _samples = new float[count];
                _bytes = new byte[count * 2];
                _currentPath = path;
                NowPlaying = DisplayName(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Skipping music {Path.GetFileName(path)}: {ex.Message}");
                StopTrack();
                _tracks.Remove(path);
                if (_tracks.Count == 0) return;
            }
        }
    }

    /// <summary>A new shuffled round of every track, not starting with the one that just played.</summary>
    private void RefillQueue()
    {
        _queue.AddRange(_tracks.OrderBy(_ => _rng.Next()));
        if (_queue.Count > 1 && _queue[0] == _lastPlayed) (_queue[0], _queue[^1]) = (_queue[^1], _queue[0]);
    }

    private void SubmitBuffer()
    {
        int read = _reader!.ReadSamples(_samples, 0, _samples.Length);
        if (read <= 0) return;
        for (int i = 0; i < read; i++)
        {
            short value = (short)(Math.Clamp(_samples[i], -1f, 1f) * short.MaxValue);
            _bytes[i * 2] = (byte)value;
            _bytes[i * 2 + 1] = (byte)(value >> 8);
        }
        _instance!.SubmitBuffer(_bytes, 0, read * 2);
    }

    private void StopTrack()
    {
        if (_currentPath != null) _lastPlayed = _currentPath;
        _currentPath = null;
        _instance?.Stop();
        _instance?.Dispose();
        _instance = null;
        _reader?.Dispose();
        _reader = null;
        NowPlaying = null;
    }

    /// <summary>
    /// "zephiramusic-lofi-trance-596433.ogg" becomes "ZEPHIRAMUSIC LOFI TRANCE": separators become spaces and a
    /// long trailing number (a download site's id) is dropped, while short ones like "theme 2" stay.
    /// </summary>
    public static string DisplayName(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[^1].Length >= 5 && parts[^1].All(char.IsDigit)) parts.RemoveAt(parts.Count - 1);
        return string.Join(' ', parts).ToUpperInvariant();
    }

    public void Dispose() => StopTrack();
}
