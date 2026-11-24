using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;
using Poiesis.Engine.Audio;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Poiesis.Audio;

/// <summary>
/// Le son de l'éditeur et du joueur, sur SDL3_mixer. Charge les banques de sons du jeu (<c>content/audio/*.cues.json</c>),
/// prépare chaque cue (synthèse ou fichier), puis joue les <see cref="SoundEvents"/> de la partie : voix limitées par
/// cue, volume selon la distance à la caméra, panoramique gauche-droite. Sans périphérique audio (serveur, CI), il se
/// rabat sur le pilote muet de SDL et tout continue.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    const uint DefaultPlaybackDevice = 0xFFFFFFFFu;
    const int Voices = 32;

    readonly string _contentRoot;
    readonly Action<string> _log;
    readonly MixMixer _mixer;
    readonly MixTrack[] _tracks;
    readonly double[] _trackEnds = new double[Voices];
    readonly Dictionary<string, PreparedCue> _cues = new(StringComparer.Ordinal);
    readonly Limiter _limiter = new();
    readonly MixPostMixHandler _postMix;
    readonly List<MixAudio> _owned = new();
    readonly VoicePolicy _policy = new();
    readonly List<SoundEvent> _pending = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly Random _random = new(1);
    DateTime _banksStamp;

    public bool Available { get; }

    /// <summary>Le dossier <c>content/</c> du jeu.</summary>
    public string ContentRoot => _contentRoot;

    /// <summary>Le pilote audio de SDL en service (« pulseaudio », « wasapi », « dummy »…).</summary>
    public string Driver { get; } = "aucun";

    /// <summary>Volume général (0 à 1), appliqué au mixeur.</summary>
    public float MasterVolume
    {
        get => _master;
        set
        {
            _master = Math.Clamp(value, 0, 1);
            if (Available)
                _mixer.SetGain(_master);
        }
    }

    float _master = 0.8f;

    /// <summary>Volume des effets de la partie (0 à 1), sous le volume général.</summary>
    public float EffectsVolume { get; set; } = 1f;

    public bool Muted { get; set; }

    /// <summary>Sons joués depuis le lancement (diagnostic, tests).</summary>
    public int Played { get; private set; }

    public IReadOnlyCollection<string> CueIds => _cues.Keys;

    /// <summary>Gain du limiteur du mixage (1 : rien n'est limité).</summary>
    public float LimiterGain => _limiter.Gain;

    /// <summary>Une cue prête : ses variantes chargées, leur durée, et la dernière jouée (pour ne pas la répéter).</summary>
    sealed class PreparedCue(Cue cue)
    {
        public Cue Cue { get; } = cue;
        public List<(MixAudio Audio, double Duration)> Variants { get; } = new();
        public int Last = -1;
        public double Duration => Variants.Count == 0 ? 0 : Variants.Max(v => v.Duration);
    }

    public AudioEngine(string contentRoot, Action<string> log)
    {
        _contentRoot = contentRoot;
        _log = log;
        _postMix = (MixMixer _, in SdlAudioSpec spec, Span<float> pcm) => _limiter.Process(pcm, spec.channels, spec.freq);
        try
        {
            if (!TryInit())
            {
                // Pas de périphérique : le pilote muet garde la même API (les sons « jouent », en silence).
                Sdl.PInvoke.SetHint(Sdl.Constants.HintAudioDriver, "dummy");
                if (!TryInit())
                    throw new InvalidOperationException("SDL ne trouve aucun pilote audio");
            }
            _mixer = SdlMixer.CreateMixerDevice(DefaultPlaybackDevice, null);
            _tracks = new MixTrack[Voices];
            for (int i = 0; i < Voices; i++)
                _tracks[i] = _mixer.CreateTrack();
            Driver = Sdl.PInvoke.GetCurrentAudioDriver() ?? "?";
            Available = true;
            _mixer.SetGain(_master);
            SdlMixer.SetPostMixCallback(_mixer, _postMix);
            ReloadBanks();
        }
        catch (Exception ex)
        {
            _tracks = [];
            _mixer = default;
            Available = false;
            _log($"Son désactivé : {ex.Message}");
        }
    }

    static bool _sdlInitialized;

    static bool TryInit()
    {
        try
        {
            if (!_sdlInitialized)
            {
                Sdl.Init.Initialize(SdlInitFlags.Audio);
                SdlMixer.Init();
                _sdlInitialized = true;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    string AudioDir => Path.Combine(_contentRoot, "audio");

    /// <summary>Les banques de sons du jeu.</summary>
    public IEnumerable<string> BankFiles() =>
        Directory.Exists(AudioDir) ? Directory.EnumerateFiles(AudioDir, "*.cues.json").Order(StringComparer.Ordinal) : [];

    /// <summary>
    /// Les crédits des sons enregistrés (auteur, licence, adresse), une ligne par son d'origine, d'après les banques.
    /// Obligatoires pour les sons sous licence CC BY ; donnés aussi pour les CC0, par courtoisie.
    /// </summary>
    public static List<string> SoundCredits(string contentRoot)
    {
        var lines = new List<string>();
        var dir = Path.Combine(contentRoot, "audio");
        if (!Directory.Exists(dir))
            return lines;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cues.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var bank = ValueCodec.Read<CueBank>(File.ReadAllText(file));
                foreach (var v in bank.Cues.SelectMany(c => c.Sound.Variants ?? []))
                    if (v.Author is not null && seen.Add(v.Url ?? v.Source ?? v.File))
                    {
                        string name = v.Source is { } s && s.IndexOf('«') is int a and >= 0 && s.IndexOf('»', a) is int b and > 0 ? s[(a + 1)..b].Trim() : v.Source ?? v.File;
                        lines.Add($"« {name} » par {v.Author} ({v.License}){(v.Url is null ? "" : $", {v.Url}")}");
                    }
            }
            catch (Exception)
            {
                // Une banque illisible n'empêche pas d'afficher les autres crédits.
            }
        }
        return lines;
    }

    /// <summary>Relit les banques (après une modification dans l'éditeur) et refait les sons.</summary>
    public void ReloadBanks()
    {
        if (!Available)
            return;
        _mixer.StopAllTracks(0);
        foreach (var a in _owned)
            a.Dispose();
        _owned.Clear();
        _cues.Clear();
        _banksStamp = Stamp();
        foreach (var file in BankFiles())
        {
            try
            {
                var bank = ValueCodec.Read<CueBank>(File.ReadAllText(file));
                foreach (var cue in bank.Cues)
                    if (Prepare(cue) is { } prepared)
                        _cues[cue.Id] = prepared;
            }
            catch (Exception ex)
            {
                _log($"{Path.GetFileName(file)} : {ex.Message}");
            }
        }
    }

    DateTime Stamp() => BankFiles().Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

    /// <summary>Relit les banques si un fichier a changé (appelé de temps en temps par l'hôte).</summary>
    public void ReloadIfChanged()
    {
        if (Available && Stamp() != _banksStamp)
            ReloadBanks();
    }

    PreparedCue? Prepare(Cue cue)
    {
        var prepared = new PreparedCue(cue);
        try
        {
            foreach (var file in cue.Sound.Files())
            {
                try
                {
                    var audio = _mixer.LoadAudio(Path.Combine(_contentRoot, file), true);
                    _owned.Add(audio);
                    prepared.Variants.Add((audio, audio.FramesToMS(audio.GetDuration()) / 1000.0));
                }
                catch (Exception ex)
                {
                    _log($"Cue « {cue.Id} », {file} : {ex.Message}");
                }
            }
            if (prepared.Variants.Count == 0 && cue.Sound.Synth is { } synth)
            {
                var samples = Synthesizer.Render(synth);
                prepared.Variants.Add((Raw(samples), samples.Length / (double)Synthesizer.SampleRate));
            }
        }
        catch (Exception ex)
        {
            _log($"Cue « {cue.Id} » : {ex.Message}");
        }
        return prepared.Variants.Count > 0 ? prepared : null;
    }

    /// <summary>
    /// Décode un fichier son (WAV, OGG, MP3, FLAC) en mono flottant à 44,1 kHz, pour l'importer ou en voir la forme
    /// d'onde. Ne demande pas de carte son : seulement SDL_mixer.
    /// </summary>
    public static float[] Decode(string path, int sampleRate = Synthesizer.SampleRate)
    {
        if (!TryInit())
            throw new InvalidOperationException("SDL_mixer indisponible");
        var decoder = SdlMixer.CreateAudioDecoder(path, 0);
        try
        {
            var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 2, freq = sampleRate };
            var all = new List<float>();
            var buffer = new float[16384];
            fixed (float* p = buffer)
            {
                while (true)
                {
                    int bytes = decoder.DecodeAudio(p, buffer.Length * sizeof(float), &spec);
                    if (bytes <= 0)
                        break;
                    all.AddRange(buffer.AsSpan(0, bytes / sizeof(float)));
                }
            }
            return AudioClean.ToMono(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all), 2);
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>Écoute d'un fichier (aperçu d'un son trouvé avant de l'importer), au centre.</summary>
    public void PreviewFile(string path, float volume = 1)
    {
        if (!Available)
            return;
        var audio = _mixer.LoadAudio(path, true);
        _owned.Add(audio);
        var track = FreeTrack();
        track.SetAudio(audio);
        track.SetGain(volume);
        track.SetFrequencyRatio(1);
        track.SetStereo(null);
        track.Play(0);
    }

    /// <summary>Écoute d'une variante précise d'une cue.</summary>
    public bool PreviewVariant(string cueId, int variant)
    {
        if (!Available || !_cues.TryGetValue(cueId, out var c) || variant < 0 || variant >= c.Variants.Count)
            return false;
        var track = FreeTrack();
        track.SetAudio(c.Variants[variant].Audio);
        track.SetGain(Math.Clamp(c.Cue.Volume, 0, 1));
        track.SetFrequencyRatio(1);
        track.SetStereo(null);
        track.Play(0);
        return true;
    }

    MixAudio Raw(float[] samples)
    {
        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 1, freq = Synthesizer.SampleRate };
        fixed (float* p = samples)
        {
            var audio = _mixer.LoadRawAudio(p, (nuint)(samples.Length * sizeof(float)), &spec);
            _owned.Add(audio);
            return audio;
        }
    }

    /// <summary>
    /// Joue les sons demandés par la partie depuis <paramref name="cursor"/>, et le fait avancer. Au plus quelques sons
    /// par image, les plus forts d'abord : à 500 contre 500, on entend la bataille, pas chaque obus.
    /// </summary>
    public void Update(World world, ref long cursor, Listener listener, int maxPerFrame = 12)
    {
        if (!world.TryResource<SoundEvents>(out var events))
            return;
        _pending.Clear();
        cursor = events.ReadSince(cursor, _pending);
        if (!Available || Muted || _pending.Count == 0)
            return;
        var ranked = _pending
            .Where(e => _cues.ContainsKey(e.Cue))
            .Select(e => (Event: e, Gain: e.Volume * _cues[e.Cue].Cue.Volume * VoicePolicy.Attenuation(_cues[e.Cue].Cue, e.Position, listener)))
            .Where(x => x.Gain > 0.01f)
            .OrderByDescending(x => x.Gain)
            .Take(maxPerFrame * 4);
        int started = 0;
        foreach (var (e, gain) in ranked)
        {
            if (started >= maxPerFrame)
                break;
            if (Start(e.Cue, gain * EffectsVolume, VoicePolicy.Pan(e.Position, listener)))
                started++;
        }
    }

    /// <summary>Joue une cue au centre, à plein volume (écoute dans l'éditeur).</summary>
    public bool Preview(string cue) => Available && Start(cue, 1, (0.7071f, 0.7071f), ignoreLimits: true);

    /// <summary>Écoute d'un son de synthèse pas encore enregistré (panneau Sons).</summary>
    public void Preview(Synth synth, float volume = 1)
    {
        if (!Available)
            return;
        var audio = Raw(Synthesizer.Render(synth));
        var track = FreeTrack();
        track.SetAudio(audio);
        track.SetGain(volume);
        track.SetFrequencyRatio(1);
        track.SetStereo(null);
        track.Play(0);
    }

    bool Start(string cueId, float gain, (float Left, float Right) pan, bool ignoreLimits = false)
    {
        if (!_cues.TryGetValue(cueId, out var c))
            return false;
        double now = _clock.Elapsed.TotalSeconds;
        int variant = c.Variants.Count == 1 ? 0 : _random.Next(c.Variants.Count - 1);
        if (c.Variants.Count > 1 && variant >= c.Last && c.Last >= 0)
            variant++;   // jamais deux fois de suite la même variante
        var (audio, duration) = c.Variants[variant];
        if (!ignoreLimits && !_policy.TryStart(c.Cue, now, duration))
            return false;
        c.Last = variant;
        var track = FreeTrack();
        track.SetAudio(audio);
        track.SetGain(Math.Clamp(gain, 0, 2));
        var gains = new MixStereoGains { left = pan.Left, right = pan.Right };
        track.SetStereo(&gains);
        float variation = c.Cue.PitchVariation;
        track.SetFrequencyRatio(1 + (float)(_random.NextDouble() * 2 - 1) * variation);
        track.Play(0);
        _trackEnds[Array.IndexOf(_tracks, track)] = now + duration;
        Played++;
        return true;
    }

    /// <summary>Une piste libre, sinon celle qui finit le plus tôt.</summary>
    MixTrack FreeTrack()
    {
        int best = 0;
        for (int i = 0; i < _tracks.Length; i++)
        {
            if (!_tracks[i].Playing())
                return _tracks[i];
            if (_trackEnds[i] < _trackEnds[best])
                best = i;
        }
        _tracks[best].Stop(0);
        return _tracks[best];
    }

    public void StopAll()
    {
        if (Available)
            _mixer.StopAllTracks(0);
    }

    public void Dispose()
    {
        if (!Available)
            return;
        _mixer.StopAllTracks(0);
        SdlMixer.SetPostMixCallback(_mixer, null);
        _mixer.Dispose();   // détruit aussi les pistes
        foreach (var a in _owned)
            a.Dispose();
        _owned.Clear();
    }
}
