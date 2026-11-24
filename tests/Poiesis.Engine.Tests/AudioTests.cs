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
    public void Orionum_cue_bank_reads_and_every_synth_is_audible()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "games", "Orionum")))
            dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "games", "Orionum", "content", "audio", "battle.cues.json");
        var bank = ValueCodec.Read<CueBank>(File.ReadAllText(file));
        Assert.Contains(bank.Cues, c => c.Id == "explosion");
        Assert.All(bank.Cues, c => Assert.True(Synthesizer.Render(c.Sound.Synth!).Max(MathF.Abs) > 0.1f, c.Id));
    }
}
