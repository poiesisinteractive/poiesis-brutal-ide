using Poiesis.Engine.Audio;

namespace Poiesis.Audio;

/// <summary>
/// Range un son importé dans le contenu du jeu : nettoyé (<see cref="AudioClean"/>), écrit en WAV sous
/// <c>content/audio/&lt;banque&gt;/&lt;cue&gt;-&lt;n&gt;.wav</c>, et décrit par une <see cref="SoundVariant"/> à ajouter à la cue.
/// </summary>
public static class SoundImport
{
    /// <summary>Écrit le son et rend sa variante (chemin relatif à <c>content/</c>), ou null s'il n'y avait que du silence.</summary>
    public static SoundVariant? Write(string contentRoot, string bankId, string cueId, float[] mono, SoundVariant meta, float maxSeconds = 4)
    {
        var clean = AudioClean.Clean(mono, Synthesizer.SampleRate, maxSeconds);
        if (clean.Length == 0)
            return null;
        string folder = Path.Combine("audio", Safe(bankId));
        int n = 1;
        while (File.Exists(Path.Combine(contentRoot, folder, $"{Safe(cueId)}-{n}.wav")))
            n++;
        string relative = Path.Combine(folder, $"{Safe(cueId)}-{n}.wav").Replace('\\', '/');
        Wav.WriteMono16(Path.Combine(contentRoot, relative), clean, Synthesizer.SampleRate);
        return meta with { File = relative };
    }

    /// <summary>Importe un fichier son (WAV, OGG, MP3, FLAC).</summary>
    public static SoundVariant? FromFile(string contentRoot, string bankId, string cueId, string path, SoundVariant meta, float maxSeconds = 4) =>
        Write(contentRoot, bankId, cueId, AudioEngine.Decode(path), meta, maxSeconds);

    /// <summary>
    /// Efface les fichiers de <c>audio/&lt;banque&gt;/</c> qu'aucune cue ne désigne plus (après la suppression d'une
    /// variante). Ne touche qu'aux WAV de ce dossier.
    /// </summary>
    public static int RemoveUnused(string contentRoot, CueBank bank)
    {
        string folder = Path.Combine(contentRoot, "audio", Safe(bank.Id));
        if (!Directory.Exists(folder))
            return 0;
        var used = bank.Cues.SelectMany(c => c.Sound.Files()).Select(f => Path.GetFullPath(Path.Combine(contentRoot, f))).ToHashSet(StringComparer.Ordinal);
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.wav"))
            if (!used.Contains(Path.GetFullPath(file)))
            {
                File.Delete(file);
                removed++;
            }
        return removed;
    }

    static string Safe(string name) =>
        string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
}
