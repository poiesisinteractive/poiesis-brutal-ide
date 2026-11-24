using Brutal.Numerics;

namespace Poiesis.Engine.Audio;

public enum Waveform { Sine, Square, Saw, Triangle }

/// <summary>
/// Un son de synthèse : un oscillateur (de <see cref="Frequency"/> à <see cref="EndFrequency"/>) mêlé de bruit filtré,
/// sous une enveloppe (attaque, puis décroissance jusqu'à <see cref="Duration"/>). De quoi faire des canons, des lasers,
/// des explosions sans fichier audio ; un fichier WAV ou OGG reste possible (<see cref="Sound.File"/>).
/// </summary>
public sealed record Synth
{
    public Waveform Waveform { get; init; } = Waveform.Sine;

    /// <summary>Fréquence au début et à la fin (Hz) : un glissement vers le grave donne un tir ou une explosion.</summary>
    public float Frequency { get; init; } = 440;
    public float EndFrequency { get; init; } = 440;

    /// <summary>Durée totale (s), attaque (s), et courbure de la décroissance (1 linéaire, plus grand : plus sec).</summary>
    public float Duration { get; init; } = 0.3f;
    public float Attack { get; init; } = 0.005f;
    public float Decay { get; init; } = 3;

    /// <summary>Part de bruit (0 : oscillateur pur, 1 : bruit seul), et coupure du filtre passe-bas sur ce bruit (0 à 1).</summary>
    public float Noise { get; init; }
    public float LowPass { get; init; } = 0.5f;

    /// <summary>Saturation douce (0 : aucune) : épaissit les canons et les explosions.</summary>
    public float Drive { get; init; }
}

/// <summary>Ce que joue une cue : un fichier (relatif à <c>content/</c>) ou un son de synthèse.</summary>
public sealed record Sound
{
    public string? File { get; init; }
    public Synth? Synth { get; init; }
}

/// <summary>
/// Une cue : un nom que le jeu demande (« canon », « explosion »), et comment le jouer. Les voix et l'intervalle évitent
/// qu'un millier de tirs simultanés ne sature le mixage.
/// </summary>
public sealed record Cue
{
    public string Id { get; init; } = "";
    public float Volume { get; init; } = 1;

    /// <summary>Variation de hauteur au hasard (0,1 : ±10 %), pour qu'un même son répété ne sonne pas mécanique.</summary>
    public float PitchVariation { get; init; } = 0.05f;

    /// <summary>Nombre maximal de voix simultanées de cette cue, et intervalle minimal entre deux départs (s).</summary>
    public int MaxVoices { get; init; } = 4;
    public float MinInterval { get; init; } = 0.04f;

    /// <summary>Distance (m) à laquelle le son a perdu la moitié de sa force ; 0 : suivre la distance de la caméra.</summary>
    public float Range { get; init; }

    public Sound Sound { get; init; } = new();
}

/// <summary>Une banque de sons, éditée dans <c>content/audio/*.cues.json</c>.</summary>
public sealed record CueBank
{
    public string Id { get; init; } = "";
    public List<Cue> Cues { get; init; } = new();
}

/// <summary>L'auditeur : la caméra (position, axe droit pour le panoramique) et sa distance au point visé.</summary>
public readonly record struct Listener(float3 Position, float3 Right, float Distance);

/// <summary>Synthèse des <see cref="Synth"/> en échantillons mono flottants, déterministe (même réglage, même son).</summary>
public static class Synthesizer
{
    public const int SampleRate = 44100;

    public static float[] Render(Synth s, int sampleRate = SampleRate)
    {
        int n = Math.Max(1, (int)(Math.Clamp(s.Duration, 0.01f, 10f) * sampleRate));
        var samples = new float[n];
        double phase = 0;
        uint noiseState = 0x9E3779B9;
        float filtered = 0;
        float cutoff = Math.Clamp(s.LowPass, 0.001f, 1f);
        float attack = MathF.Max(s.Attack, 1e-4f);
        float noise = Math.Clamp(s.Noise, 0, 1);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sampleRate;
            float u = i / (float)n;
            float freq = s.Frequency + (s.EndFrequency - s.Frequency) * u;
            phase += freq / sampleRate;
            phase -= Math.Floor(phase);
            float p = (float)phase;
            float osc = s.Waveform switch
            {
                Waveform.Square => p < 0.5f ? 1 : -1,
                Waveform.Saw => 2 * p - 1,
                Waveform.Triangle => 1 - 4 * MathF.Abs(p - 0.5f),
                _ => MathF.Sin(p * MathF.Tau),
            };
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            float white = noiseState / (float)uint.MaxValue * 2 - 1;
            filtered += (white - filtered) * cutoff;   // passe-bas à un pôle
            float v = osc * (1 - noise) + filtered * noise * (1 + (1 - cutoff) * 2);   // compense la perte du filtre
            float env = t < attack ? t / attack : MathF.Pow(1 - (t - attack) / MathF.Max(s.Duration - attack, 1e-4f), MathF.Max(s.Decay, 0.1f));
            v *= MathF.Max(env, 0);
            if (s.Drive > 0)
                v = MathF.Tanh(v * (1 + s.Drive * 4)) / MathF.Tanh(1 + s.Drive * 4);
            samples[i] = Math.Clamp(v, -1, 1);
        }
        return samples;
    }
}

/// <summary>
/// Qui joue, et à quel volume : voix par cue, intervalle minimal, atténuation et panoramique. Sans carte son, pour être
/// testé ; le moteur audio applique ses décisions.
/// </summary>
public sealed class VoicePolicy
{
    readonly Dictionary<string, double> _lastStart = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<double>> _ends = new(StringComparer.Ordinal);

    /// <summary>
    /// Vrai si <paramref name="cue"/> peut partir à l'instant <paramref name="now"/> (s), pour une durée donnée ; note
    /// alors la voix. Faux si la cue vient de partir ou a déjà toutes ses voix.
    /// </summary>
    public bool TryStart(Cue cue, double now, double duration)
    {
        if (_lastStart.TryGetValue(cue.Id, out var last) && now - last < cue.MinInterval)
            return false;
        if (!_ends.TryGetValue(cue.Id, out var ends))
            _ends[cue.Id] = ends = new List<double>();
        ends.RemoveAll(e => e <= now);
        if (ends.Count >= Math.Max(cue.MaxVoices, 1))
            return false;
        ends.Add(now + duration);
        _lastStart[cue.Id] = now;
        return true;
    }

    /// <summary>Gain dû à la distance : 1 tout près, ½ à la portée de la cue (ou à la distance de la caméra).</summary>
    public static float Attenuation(Cue cue, float3 at, Listener listener)
    {
        float range = cue.Range > 0 ? cue.Range : MathF.Max(listener.Distance, 100);
        float d = float3.Distance(at, listener.Position) / range;
        return 1 / (1 + d * d);
    }

    /// <summary>Gains gauche et droite (loi à puissance constante) d'après la position latérale du son.</summary>
    public static (float Left, float Right) Pan(float3 at, Listener listener)
    {
        var to = at - listener.Position;
        float len = to.Length();
        float x = len > 1e-3f ? Math.Clamp(float3.Dot(to / len, listener.Right), -1, 1) * 0.8f : 0;
        float a = (x + 1) * MathF.PI / 4;
        return (MathF.Cos(a), MathF.Sin(a));
    }
}
