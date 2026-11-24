namespace Poiesis.Engine.Audio;

/// <summary>
/// Préparation d'un son importé (fichier, Freesound, génération) : passage en mono sans perte de phase, silence de tête
/// et de queue coupés, durée bornée avec un fondu, et volume ramené à une même intensité perçue. Ainsi deux variantes
/// d'une cue sonnent aussi fort l'une que l'autre, et le volume de la cue règle seul le mixage.
/// </summary>
public static class AudioClean
{
    /// <summary>Intensité visée : la tranche de 50 ms la plus forte, en valeur efficace (dBFS).</summary>
    public const float TargetLoudnessDb = -14;

    /// <summary>Crête maximale après normalisation (dBFS).</summary>
    public const float PeakCeilingDb = -1;

    /// <summary>
    /// Mono à partir d'échantillons entrelacés. La somme des canaux, sauf si elle s'annule en partie (stéréo en opposition
    /// de phase, fréquente dans les sons « larges ») : on garde alors le canal le plus fort.
    /// </summary>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
            return interleaved.ToArray();
        int frames = interleaved.Length / channels;
        var mid = new float[frames];
        var energy = new double[channels];
        double midEnergy = 0;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                float v = interleaved[f * channels + c];
                sum += v;
                energy[c] += v * v;
            }
            mid[f] = sum / channels;
            midEnergy += mid[f] * (double)mid[f];
        }
        double average = energy.Average();
        if (average <= 0 || midEnergy >= average * 0.5)   // moins de 3 dB perdus : la somme est fidèle
            return mid;
        int best = Array.IndexOf(energy, energy.Max());
        var one = new float[frames];
        for (int f = 0; f < frames; f++)
            one[f] = interleaved[f * channels + best];
        return one;
    }

    /// <summary>Le son prêt à jouer ; vide s'il n'y a que du silence.</summary>
    public static float[] Clean(float[] mono, int sampleRate, float maxSeconds = 4)
    {
        if (mono.Length == 0)
            return [];
        float peak = 0;
        foreach (var v in mono)
            peak = MathF.Max(peak, MathF.Abs(v));
        if (peak < 1e-5f)
            return [];

        // Silence de tête : jusqu'au premier échantillon à -40 dB de la crête, moins 3 ms pour garder l'attaque entière.
        float headThreshold = peak * Db(-40);
        int start = 0;
        while (start < mono.Length && MathF.Abs(mono[start]) < headThreshold)
            start++;
        start = Math.Max(0, start - sampleRate * 3 / 1000);

        // Queue : la dernière tranche de 10 ms qui dépasse -55 dB de la crête.
        int window = Math.Max(1, sampleRate / 100);
        float tailThreshold = peak * Db(-55);
        int end = mono.Length;
        while (end - window > start && Rms(mono, end - window, window) < tailThreshold)
            end -= window;
        end = Math.Min(end, start + (int)(maxSeconds * sampleRate));

        var output = mono.AsSpan(start, end - start).ToArray();
        if (output.Length == 0)
            return [];

        // Fondus : 1 ms à l'entrée (pas de clic), 10 % de la durée en sortie (au moins 20 ms, au plus 300 ms).
        int fadeIn = Math.Min(output.Length, sampleRate / 1000);
        for (int i = 0; i < fadeIn; i++)
            output[i] *= i / (float)fadeIn;
        int fadeOut = Math.Min(output.Length, Math.Clamp(output.Length / 10, sampleRate / 50, sampleRate * 3 / 10));
        for (int i = 0; i < fadeOut; i++)
        {
            float u = i / (float)fadeOut;
            output[output.Length - 1 - i] *= u * u;
        }

        // Intensité : la tranche de 50 ms la plus forte à la valeur visée, sans dépasser le plafond de crête.
        int loudWindow = Math.Max(1, sampleRate / 20);
        float loudest = 0;
        for (int i = 0; i < output.Length; i += loudWindow / 2)
            loudest = MathF.Max(loudest, Rms(output, i, Math.Min(loudWindow, output.Length - i)));
        float newPeak = 0;
        foreach (var v in output)
            newPeak = MathF.Max(newPeak, MathF.Abs(v));
        float gain = MathF.Min(Db(TargetLoudnessDb) / MathF.Max(loudest, 1e-6f), Db(PeakCeilingDb) / MathF.Max(newPeak, 1e-6f));
        for (int i = 0; i < output.Length; i++)
            output[i] *= gain;
        return output;
    }

    /// <summary>
    /// Transpose un son comme une bande ralentie ou accélérée : <paramref name="pitch"/> 0,5 le rend une octave plus grave
    /// et deux fois plus long (c'est ainsi qu'un blaster devient un canon lourd). Interpolation cubique (Hermite).
    /// </summary>
    public static float[] Resample(float[] samples, float pitch)
    {
        if (MathF.Abs(pitch - 1) < 1e-4f || samples.Length == 0)
            return samples;
        pitch = Math.Clamp(pitch, 0.1f, 4f);
        int n = (int)(samples.Length / pitch);
        var output = new float[n];
        float At(int i) => samples[Math.Clamp(i, 0, samples.Length - 1)];
        for (int i = 0; i < n; i++)
        {
            double x = i * (double)pitch;
            int k = (int)x;
            float t = (float)(x - k);
            float y0 = At(k - 1), y1 = At(k), y2 = At(k + 1), y3 = At(k + 2);
            float c1 = 0.5f * (y2 - y0), c2 = y0 - 2.5f * y1 + 2 * y2 - 0.5f * y3, c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            output[i] = ((c3 * t + c2) * t + c1) * t + y1;
        }
        return output;
    }

    /// <summary>Valeur efficace de <paramref name="count"/> échantillons à partir de <paramref name="from"/>.</summary>
    public static float Rms(float[] samples, int from, int count)
    {
        if (count <= 0)
            return 0;
        double sum = 0;
        for (int i = from; i < from + count; i++)
            sum += samples[i] * (double)samples[i];
        return (float)Math.Sqrt(sum / count);
    }

    public static float Db(float db) => MathF.Pow(10, db / 20);

    /// <summary>Enveloppe pour un aperçu : le maximum absolu par tranche.</summary>
    public static float[] Envelope(float[] samples, int buckets)
    {
        var wave = new float[buckets];
        if (samples.Length == 0)
            return wave;
        for (int b = 0; b < buckets; b++)
        {
            int from = b * samples.Length / buckets, to = Math.Max(from + 1, (b + 1) * samples.Length / buckets);
            float m = 0;
            for (int i = from; i < to && i < samples.Length; i++)
                m = MathF.Max(m, MathF.Abs(samples[i]));
            wave[b] = m;
        }
        return wave;
    }
}

/// <summary>WAV PCM 16 bits mono : le format des sons importés (lu partout, sans perte supplémentaire).</summary>
public static class Wav
{
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        // Dither triangulaire : pas de distorsion de quantification sur les queues de réverbération.
        uint state = 0x2545F491;
        foreach (var v in samples)
        {
            float dither = (Next(ref state) - Next(ref state)) / 32768f;
            w.Write((short)Math.Clamp(MathF.Round(v * 32767 + dither * 32767 / 32768f), -32768, 32767));
        }
    }

    static float Next(ref uint s)
    {
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        return s / (float)uint.MaxValue;
    }

    /// <summary>Lit un WAV PCM 16 bits (mono ou stéréo, rendu entrelacé).</summary>
    public static (float[] Samples, int Channels, int SampleRate) Read16(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int channels = 1, rate = 44100;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadBytes(size - 8);
            }
            else if (id == "data")
            {
                var samples = new float[size / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = r.ReadInt16() / 32768f;
                return (samples, channels, rate);
            }
            else
                r.ReadBytes(size);
        }
        return ([], channels, rate);
    }
}

/// <summary>
/// Limiteur du mixage final : quand des dizaines de tirs et d'explosions se superposent, il baisse le gain (aussitôt)
/// puis le rend (en 150 ms), au lieu de laisser le signal écrêter en grésillement. Canaux liés : l'image stéréo ne bouge
/// pas. Un dernier arrondi doux rattrape ce qui passerait entre deux échantillons.
/// </summary>
public sealed class Limiter
{
    readonly float _threshold;
    float _gain = 1;

    public Limiter(float thresholdDb = -1.5f) => _threshold = AudioClean.Db(thresholdDb);

    /// <summary>Gain en cours (1 : rien n'est limité), pour le diagnostic.</summary>
    public float Gain => _gain;

    public void Process(Span<float> interleaved, int channels, int sampleRate)
    {
        channels = Math.Max(1, channels);
        float release = 1 - MathF.Exp(-1f / (0.15f * sampleRate));
        for (int f = 0; f + channels <= interleaved.Length; f += channels)
        {
            float peak = 0;
            for (int c = 0; c < channels; c++)
                peak = MathF.Max(peak, MathF.Abs(interleaved[f + c]));
            float wanted = peak * _gain > _threshold ? _threshold / peak : 1;
            _gain = wanted < _gain ? wanted : _gain + (MathF.Min(wanted, 1) - _gain) * release;
            for (int c = 0; c < channels; c++)
                interleaved[f + c] = SoftClip(interleaved[f + c] * _gain);
        }
    }

    /// <summary>Linéaire jusqu'à 0,9, puis une courbe qui tend vers 1 sans l'atteindre.</summary>
    public static float SoftClip(float v)
    {
        float a = MathF.Abs(v);
        if (a <= 0.9f)
            return v;
        float over = (a - 0.9f) / 0.1f;
        return MathF.CopySign(0.9f + 0.1f * MathF.Tanh(over), v);
    }
}
