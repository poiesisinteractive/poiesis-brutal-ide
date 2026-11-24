using Brutal.Numerics;
using Poiesis.Engine.Audio;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

public class AudioTests
{
    [Fact]
    public void A_synth_renders_its_duration_within_range_and_fades_out()
    {
        var synth = new Synth { Waveform = Waveform.Saw, Frequency = 180, EndFrequency = 60, Duration = 0.5f, Noise = 0.5f, Drive = 0.6f };
        var a = Synthesizer.Render(synth);
        Assert.Equal(Synthesizer.SampleRate / 2, a.Length);
        Assert.All(a, v => Assert.InRange(v, -1f, 1f));
        Assert.True(a.Max(MathF.Abs) > 0.3f, "le son est trop faible");
        Assert.True(a[^100..].Max(MathF.Abs) < 0.05f, "le son ne s'éteint pas");
        Assert.Equal(a, Synthesizer.Render(synth));   // déterministe
    }

    [Fact]
    public void Voices_are_limited_per_cue_and_spaced_by_the_minimum_interval()
    {
        var cue = new Cue { Id = "canon", MaxVoices = 2, MinInterval = 0.1f };
        var policy = new VoicePolicy();
        Assert.True(policy.TryStart(cue, 0.0, 1.0));
        Assert.False(policy.TryStart(cue, 0.05, 1.0));    // trop tôt
        Assert.True(policy.TryStart(cue, 0.2, 1.0));
        Assert.False(policy.TryStart(cue, 0.4, 1.0));     // deux voix déjà
        Assert.True(policy.TryStart(cue, 1.1, 1.0));      // la première a fini
    }

    [Fact]
    public void Sounds_fade_with_distance_and_pan_to_their_side()
    {
        var listener = new Listener(float3.Zero, new float3(1, 0, 0), 1000);
        var cue = new Cue();
        Assert.Equal(1, VoicePolicy.Attenuation(cue, float3.Zero, listener), 0.001f);
        Assert.Equal(0.5f, VoicePolicy.Attenuation(cue, new float3(0, 0, 1000), listener), 0.001f);
        var (left, right) = VoicePolicy.Pan(new float3(500, 0, 0), listener);
        Assert.True(right > left);
        var (l2, r2) = VoicePolicy.Pan(new float3(0, 0, 500), listener);
        Assert.Equal(l2, r2, 0.001f);
    }

    [Fact]
    public void Sound_events_are_read_since_a_cursor_and_old_ones_are_overwritten()
    {
        var events = new SoundEvents(capacity: 4);
        var read = new List<SoundEvent>();
        long cursor = events.ReadSince(0, read);
        for (int i = 0; i < 6; i++)
            events.Play($"s{i}", float3.Zero);
        cursor = events.ReadSince(cursor, read);
        Assert.Equal(["s2", "s3", "s4", "s5"], read.Select(e => e.Cue));   // les deux plus anciens sont perdus
        Assert.Equal(6, cursor);
        read.Clear();
        events.Play("s6", float3.Zero);
        events.ReadSince(cursor, read);
        Assert.Equal(["s6"], read.Select(e => e.Cue));
    }

    [Fact]
    public void Orionum_cue_bank_plays_recorded_variants_that_exist_and_are_credited()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "games", "Orionum")))
            dir = dir.Parent;
        var content = Path.Combine(dir!.FullName, "games", "Orionum", "content");
        var bank = ValueCodec.Read<CueBank>(File.ReadAllText(Path.Combine(content, "audio", "battle.cues.json")));
        Assert.Contains(bank.Cues, c => c.Id == "explosion");
        Assert.All(bank.Cues, c =>
        {
            Assert.True(c.Sound.Variants!.Count >= 2, $"{c.Id} : au moins deux variantes");
            foreach (var v in c.Sound.Variants!)
            {
                var (samples, channels, rate) = Wav.Read16(Path.Combine(content, v.File));
                Assert.Equal((1, Synthesizer.SampleRate), (channels, rate));
                Assert.InRange(samples.Max(MathF.Abs), AudioClean.Db(-12), AudioClean.Db(AudioClean.PeakCeilingDb) + 0.01f);
                Assert.Contains(v.License, new[] { "CC0", "CC BY 3.0", "CC BY 4.0" });   // jamais « non commercial »
                Assert.NotNull(v.Author);
                Assert.StartsWith("https://freesound.org/", v.Url);
            }
        });
    }

    [Fact]
    public void Resampling_lowers_the_pitch_and_stretches_the_sound()
    {
        const int rate = Synthesizer.SampleRate;
        var tone = Tone(1, 0.5f, frequency: 1000);
        var lower = AudioClean.Resample(tone, 0.5f);
        Assert.InRange(lower.Length, 2 * rate - 2, 2 * rate);
        int crossings = 0;
        for (int i = 1; i < rate; i++)
            if (lower[i - 1] < 0 != lower[i] < 0)
                crossings++;
        Assert.InRange(crossings, 990, 1010);      // 500 Hz : 1 000 passages par zéro par seconde
        Assert.Same(tone, AudioClean.Resample(tone, 1));
    }

    static float[] Tone(float seconds, float amplitude, float frequency = 220, int rate = Synthesizer.SampleRate) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => amplitude * MathF.Sin(MathF.Tau * frequency * i / rate)).ToArray();

    [Fact]
    public void Cleaning_trims_silence_bounds_duration_and_evens_out_loudness()
    {
        const int rate = Synthesizer.SampleRate;
        var quiet = new float[rate / 2].Concat(Tone(1, 0.05f)).Concat(new float[rate]).ToArray();
        var loud = Tone(6, 0.9f);

        var a = AudioClean.Clean(quiet, rate);
        var b = AudioClean.Clean(loud, rate, maxSeconds: 2);

        Assert.InRange(a.Length, rate * 0.99, rate * 1.02);              // demi-seconde de tête et seconde de queue coupées
        Assert.Equal(2 * rate, b.Length);                                 // borné à 2 s
        Assert.Equal(0, b[^1], 3);                                        // fondu de sortie
        float loudA = AudioClean.Rms(a, rate / 4, rate / 20), loudB = AudioClean.Rms(b, rate / 4, rate / 20);
        Assert.Equal(AudioClean.TargetLoudnessDb, 20 * MathF.Log10(loudA), 0.5);
        Assert.Equal(loudA, loudB, 0.01);                                 // même intensité, quel que soit le niveau d'origine
        Assert.Empty(AudioClean.Clean(new float[rate], rate));            // le silence ne s'importe pas
    }

    [Fact]
    public void Mono_keeps_out_of_phase_stereo_audible()
    {
        var tone = Tone(0.2f, 0.5f);
        var inPhase = tone.SelectMany(v => new[] { v, v }).ToArray();
        var opposed = tone.SelectMany(v => new[] { v, -v * 0.9f }).ToArray();

        Assert.Equal(tone, AudioClean.ToMono(inPhase, 2));
        var mono = AudioClean.ToMono(opposed, 2);
        Assert.Equal(tone.Max(MathF.Abs), mono.Max(MathF.Abs), 3);         // le canal gauche, pas une somme qui s'annule
    }

    [Fact]
    public void The_limiter_holds_a_pile_up_under_full_scale_then_lets_go()
    {
        const int rate = 48000;
        var limiter = new Limiter();
        var pileUp = Tone(0.5f, 3f, rate: rate).SelectMany(v => new[] { v, v }).ToArray();   // trois fois la pleine échelle
        limiter.Process(pileUp, 2, rate);
        Assert.True(pileUp.Max(MathF.Abs) < 1);
        Assert.True(limiter.Gain < 0.35f);

        var calm = Tone(1, 0.2f, rate: rate).SelectMany(v => new[] { v, v }).ToArray();
        limiter.Process(calm, 2, rate);
        Assert.True(limiter.Gain > 0.99f);                               // relâché en moins d'une seconde
        Assert.Equal(0.2f, calm[^2000..].Max(MathF.Abs), 2);              // un son calme passe intact
    }

    [Fact]
    public void Wav_files_round_trip_and_variants_win_over_single_files()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poiesis-{Guid.NewGuid():N}.wav");
        try
        {
            var tone = Tone(0.1f, 0.5f);
            Wav.WriteMono16(path, tone, Synthesizer.SampleRate);
            var (samples, channels, rate) = Wav.Read16(path);
            Assert.Equal((1, Synthesizer.SampleRate, tone.Length), (channels, rate, samples.Length));
            Assert.All(samples.Zip(tone), p => Assert.True(MathF.Abs(p.First - p.Second) < 2e-4f));   // 16 bits et dither
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(["a.wav", "b.wav"], new Sound { File = "old.wav", Variants = [new() { File = "a.wav" }, new() { File = "b.wav" }] }.Files());
        Assert.Equal(["old.wav"], new Sound { File = "old.wav" }.Files());
        Assert.Empty(new Sound { Synth = new Synth() }.Files());
    }
}
