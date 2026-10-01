using Microsoft.Xna.Framework.Audio;

namespace XPilot.Desktop.Audio;

/// <summary>
/// Retro sound effects synthesized at startup, so the game ships without audio assets.
/// If no audio device is available, every call becomes a no-op.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private const int SampleRate = 22050;

    private readonly Dictionary<string, SoundEffect> _sounds = [];
    private readonly Random _rng = new(99);
    private SoundEffectInstance? _thrust;
    private float _thrustLevel;

    private SoundBank(float volume) => Volume = volume;

    public float Volume { get; set; }
    public bool Enabled { get; private set; }

    public static SoundBank Create(float volume)
    {
        var bank = new SoundBank(volume);
        try
        {
            bank.Generate();
            bank.Enabled = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audio disabled: {ex.Message}");
        }
        return bank;
    }

    public void Play(string name, float volume = 1f, float pitch = 0f, float pan = 0f)
    {
        if (!Enabled || Volume <= 0f || volume <= 0.01f || !_sounds.TryGetValue(name, out var sound)) return;
        try
        {
            sound.Play(Math.Clamp(volume * Volume, 0f, 1f), Math.Clamp(pitch, -1f, 1f), Math.Clamp(pan, -1f, 1f));
        }
        catch (InstancePlayLimitException)
        {
        }
    }

    /// <summary>Sets the looping engine sound level (0 = silent).</summary>
    public void SetThrust(float target, float dt)
    {
        if (!Enabled || _thrust == null) return;
        _thrustLevel += (target - _thrustLevel) * MathF.Min(1f, dt * 12f);
        float volume = Math.Clamp(_thrustLevel * Volume * 0.35f, 0f, 1f);
        if (volume < 0.005f)
        {
            if (_thrust.State == SoundState.Playing) _thrust.Pause();
            return;
        }
        _thrust.Volume = volume;
        if (_thrust.State != SoundState.Playing) _thrust.Play();
    }

    public void StopAll()
    {
        _thrustLevel = 0f;
        if (Enabled) _thrust?.Stop();
    }

    private void Generate()
    {
        Add("fire", Synth(0.09f, (t, ph) => Square(ph) * Decay(t, 0.09f, 3f) * 0.35f, t => Lerp(1400f, 450f, t / 0.09f)));
        Add("explosion", Noise(0.9f, cutoffStart: 0.6f, cutoffEnd: 0.02f, decay: 4f, gain: 0.9f));
        Add("bounce", Synth(0.12f, (t, ph) => MathF.Sin(ph) * Decay(t, 0.12f, 4f) * 0.7f, t => Lerp(140f, 60f, t / 0.12f)));
        Add("shield", Synth(0.18f, (t, ph) => (MathF.Sin(ph) + 0.5f * MathF.Sin(ph * 1.47f)) * Decay(t, 0.18f, 5f) * 0.4f, _ => 900f));
        Add("checkpoint", Chime([880f, 1320f], 0.09f));
        Add("lap", Chime([660f, 880f, 1100f, 1320f], 0.09f));
        Add("beep", Synth(0.15f, (t, ph) => MathF.Sin(ph) * Decay(t, 0.15f, 2f) * 0.5f, _ => 600f));
        Add("go", Synth(0.45f, (t, ph) => MathF.Sin(ph) * Decay(t, 0.45f, 2f) * 0.5f, _ => 1200f));
        Add("spawn", Synth(0.3f, (t, ph) => Square(ph) * Decay(t, 0.3f, 2f) * 0.15f, t => Lerp(300f, 900f, t / 0.3f)));
        Add("select", Synth(0.05f, (t, ph) => Square(ph) * Decay(t, 0.05f, 2f) * 0.2f, _ => 1000f));

        var engine = Noise(1.0f, cutoffStart: 0.08f, cutoffEnd: 0.08f, decay: 0f, gain: 1.4f);
        _sounds["thrust"] = engine;
        _thrust = engine.CreateInstance();
        _thrust.IsLooped = true;
        _thrust.Volume = 0f;
    }

    private void Add(string name, SoundEffect effect) => _sounds[name] = effect;

    /// <summary>Oscillator with a time-varying frequency; the generator receives time and phase.</summary>
    private static SoundEffect Synth(float duration, Func<float, float, float> sample, Func<float, float> frequency)
    {
        int n = (int)(duration * SampleRate);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            phase += MathF.Tau * frequency(t) / SampleRate;
            data[i] = sample(t, phase);
        }
        return ToEffect(data);
    }

    private SoundEffect Noise(float duration, float cutoffStart, float cutoffEnd, float decay, float gain)
    {
        int n = (int)(duration * SampleRate);
        var data = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float cutoff = Lerp(cutoffStart, cutoffEnd, t / duration);
            lp += (((float)_rng.NextDouble() * 2f - 1f) - lp) * cutoff;
            float env = decay > 0f ? Decay(t, duration, decay) : 1f;
            data[i] = lp * env * gain;
        }
        if (decay <= 0f)
        {
            // Crossfade the ends so the loop has no click.
            int fade = SampleRate / 20;
            for (int i = 0; i < fade; i++)
            {
                float w = i / (float)fade;
                data[i] = data[i] * w + data[n - fade + i] * (1f - w);
            }
            Array.Resize(ref data, n - fade);
        }
        return ToEffect(data);
    }

    private static SoundEffect Chime(float[] notes, float noteLength)
    {
        int perNote = (int)(noteLength * SampleRate);
        int n = perNote * notes.Length + SampleRate / 5;
        var data = new float[n];
        for (int k = 0; k < notes.Length; k++)
        {
            for (int i = 0; i < n - k * perNote; i++)
            {
                float t = i / (float)SampleRate;
                data[k * perNote + i] += MathF.Sin(MathF.Tau * notes[k] * t) * MathF.Exp(-t * 9f) * 0.3f;
            }
        }
        return ToEffect(data);
    }

    private static SoundEffect ToEffect(float[] data)
    {
        var bytes = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            short v = (short)(Math.Clamp(data[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return new SoundEffect(bytes, SampleRate, AudioChannels.Mono);
    }

    private static float Square(float phase) => MathF.Sin(phase) >= 0f ? 1f : -1f;
    private static float Decay(float t, float duration, float sharpness) => MathF.Exp(-t / duration * sharpness) * MathF.Min(1f, t * 400f);
    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    public void Dispose()
    {
        _thrust?.Dispose();
        foreach (var s in _sounds.Values) s.Dispose();
    }
}
