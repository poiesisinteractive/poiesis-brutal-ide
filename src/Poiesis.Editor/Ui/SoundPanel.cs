using System.Text.Encodings.Web;
using System.Text.Json;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.AI.Sounds;
using Poiesis.Audio;
using Poiesis.Engine.Audio;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Le panneau « Sons » : les banques de sons du jeu (<c>content/audio/*.cues.json</c>), cue par cue. Une cue joue des
/// enregistrements (plusieurs variantes, tirées au hasard) ou un son de synthèse. On cherche des sons sur Freesound, on
/// les écoute, on les ajoute ; on importe un fichier ; on en génère avec ElevenLabs. Chaque son ajouté est nettoyé
/// (silence coupé, volume homogène) et rangé dans <c>content/audio/&lt;banque&gt;/</c> avec son auteur et sa licence.
/// </summary>
sealed class SoundPanel
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly AudioEngine _audio;
    readonly LogBuffer _log;
    readonly string _cacheDir;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    readonly FreesoundClient? _freesound;
    readonly ElevenLabsSounds? _elevenLabs;

    string? _file;
    CueBank? _bank;
    int _selected;
    bool _dirty;

    // Forme d'onde : de la synthèse, ou de la variante choisie.
    float[] _wave = [];
    object? _waveFor;
    int _variant;

    // Travail en cours (recherche, téléchargement, génération) : une tâche à la fois, relevée à chaque image.
    Task? _job;
    string _jobLabel = "";
    Action? _jobDone;

    string _query = "";
    bool _publicDomainOnly = true;
    List<FreesoundResult> _results = [];
    string _importPath = "";
    string _prompt = "";
    float _genDuration = 1.5f;
    float _genInfluence = 0.5f;
    float _maxSeconds = 4;
    float _pitch = 1;

    public SoundPanel(AudioEngine audio, LogBuffer log, string cacheDir)
    {
        _audio = audio;
        _log = log;
        _cacheDir = cacheDir;
        _freesound = FreesoundClient.FromEnvironment(_http);
        _elevenLabs = ElevenLabsSounds.FromEnvironment(_http);
    }

    public void Draw()
    {
        Poll();
        if (ImGui.Begin("Sons"u8))
            DrawContent();
        ImGui.End();
    }

    void Poll()
    {
        if (_job is not { IsCompleted: true } job)
            return;
        _job = null;
        if (job.IsFaulted)
            _log.Error("son", $"{_jobLabel} : {job.Exception!.GetBaseException().Message}");
        else
            _jobDone?.Invoke();
        _jobDone = null;
    }

    void Run(string label, Task task, Action? done = null)
    {
        _jobLabel = label;
        _job = task;
        _jobDone = done;
    }

    bool Busy => _job is not null;

    void DrawContent()
    {
        if (!_audio.Available)
            ImGui.TextColored(EditorTheme.Warning, "Son indisponible : les banques s'éditent, mais rien ne s'entend."u8);
        else
            ImGui.TextDisabled($"Pilote audio : {_audio.Driver} · {_audio.CueIds.Count} cues prêtes · {_audio.Played} sons joués · limiteur {20 * MathF.Log10(MathF.Max(_audio.LimiterGain, 1e-4f)):0.0} dB");

        float master = _audio.MasterVolume;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Volume général"u8, ref master, 0, 1, "%.2f"u8))
            _audio.MasterVolume = master;
        ImGui.SameLine();
        bool muted = _audio.Muted;
        if (ImGui.Checkbox("Muet en jeu"u8, ref muted))
            _audio.Muted = muted;

        var files = _audio.BankFiles().ToList();
        if (_file is null && files.Count > 0)
            Open(files[0]);
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Banque"u8, _file is null ? "(aucune)" : Path.GetFileName(_file)))
        {
            foreach (var f in files)
                if (ImGui.Selectable(Path.GetFileName(f), f == _file))
                    Open(f);
            ImGui.EndCombo();
        }
        if (_bank is null)
        {
            ImGui.TextDisabled("Aucune banque dans content/audio/."u8);
            return;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!_dirty);
        if (ImGui.Button("Enregistrer"u8))
            Save();
        ImGui.EndDisabled();
        if (_dirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(EditorTheme.Warning, "modifiée"u8);
        }
        if (Busy)
        {
            ImGui.SameLine();
            ImGui.TextColored(EditorTheme.Accent, $"{_jobLabel}…");
        }

        if (ImGui.BeginChild("cues"u8, new float2(170, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX))
        {
            for (int i = 0; i < _bank.Cues.Count; i++)
            {
                var c = _bank.Cues[i];
                int count = c.Sound.Files().Count();
                if (ImGui.Selectable($"{c.Id}  {(count > 0 ? $"({count})" : "(synth.)")}##{i}", i == _selected))
                    Select(i);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    _audio.Preview(c.Id);
            }
            if (ImGui.SmallButton("+ Cue"u8))
            {
                _bank.Cues.Add(new Cue { Id = $"son{_bank.Cues.Count + 1}", Sound = new Sound { Variants = [] } });
                Select(_bank.Cues.Count - 1);
                _dirty = true;
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("cue"u8) && _selected < _bank.Cues.Count)
            DrawCue(_bank.Cues[_selected]);
        ImGui.EndChild();
    }

    void Select(int index)
    {
        _selected = index;
        _variant = 0;
        _waveFor = null;
    }

    void Open(string file)
    {
        try
        {
            _bank = ValueCodec.Read<CueBank>(File.ReadAllText(file));
            _file = file;
            Select(0);
            _dirty = false;
        }
        catch (Exception ex)
        {
            _log.Error("son", $"{Path.GetFileName(file)} : {ex.Message}");
        }
    }

    void Save()
    {
        File.WriteAllText(_file!, ValueCodec.ToJson(_bank, typeof(CueBank))!.ToJsonString(Json) + "\n");
        _dirty = false;
        int removed = SoundImport.RemoveUnused(_audio.ContentRoot, _bank!);
        _audio.ReloadBanks();
        _waveFor = null;
        _log.Info("son", $"Banque enregistrée : {Path.GetFileName(_file)} ({_bank!.Cues.Count} cues){(removed > 0 ? $", {removed} fichier(s) inutilisé(s) effacé(s)" : "")}, sons refaits.");
    }

    void Replace(Cue old, Cue updated)
    {
        if (old == updated)
            return;
        _bank!.Cues[_selected] = updated;
        _dirty = true;
    }

    Cue Updated(Cue old, Cue updated)
    {
        Replace(old, updated);
        return updated;
    }

    static float Slider(string label, float v, float min, float max, string format = "%.3f", bool log = false)
    {
        ImGui.SliderFloat(label, ref v, min, max, format, log ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None);
        return v;
    }

    void DrawCue(Cue cue)
    {
        string id = cue.Id;
        if (Widgets.TextField("Nom", $"cue-id-{_selected}", ref id) && id.Length > 0)
            Replace(cue, cue with { Id = id });
        cue = _bank!.Cues[_selected];
        int voices = cue.MaxVoices;
        ImGui.SliderInt("Voix"u8, ref voices, 1, 16);
        cue = Updated(cue, cue with
        {
            Volume = Slider("Volume", cue.Volume, 0, 2, "%.2f"),
            PitchVariation = Slider("Variation de hauteur", cue.PitchVariation, 0, 0.5f, "%.2f"),
            MaxVoices = voices,
            MinInterval = Slider("Intervalle minimal (s)", cue.MinInterval, 0, 1, "%.2f"),
            Range = Slider("Portée (m, 0 : caméra)", cue.Range, 0, 200_000, "%.0f"),
        });

        if (ImGui.Button("Écouter"u8, new float2(100, 0)))
            Listen(cue);
        ImGui.SetItemTooltip("Une variante au hasard, comme en jeu (double-clic sur la cue aussi)."u8);
        ImGui.SameLine();
        if (ImGui.Button("Supprimer la cue"u8))
        {
            _bank.Cues.RemoveAt(_selected);
            Select(Math.Max(0, _selected - 1));
            _dirty = true;
            return;
        }

        ImGui.SeparatorText("Son"u8);
        bool synthMode = cue.Sound.Synth is not null && !cue.Sound.Files().Any();
        if (ImGui.RadioButton("Enregistrements"u8, !synthMode) && synthMode)
            cue = Updated(cue, cue with { Sound = new Sound { Variants = [] } });
        ImGui.SameLine();
        if (ImGui.RadioButton("Synthèse"u8, synthMode) && !synthMode)
            cue = Updated(cue, cue with { Sound = new Sound { Synth = new Synth() } });

        if (synthMode)
            DrawSynth(cue, cue.Sound.Synth!);
        else
            DrawRecordings(cue);
    }

    void Listen(Cue cue)
    {
        if (_dirty && cue.Sound.Synth is { } synth && !cue.Sound.Files().Any())
            _audio.Preview(synth, cue.Volume);
        else if (!_audio.Preview(cue.Id) && _dirty)
            _log.Warn("son", "Enregistrez la banque pour entendre cette cue.");
    }

    void DrawSynth(Cue cue, Synth s)
    {
        var updated = s;
        if (ImGui.BeginCombo("Forme d'onde"u8, s.Waveform.ToString()))
        {
            foreach (var w in Enum.GetValues<Waveform>())
                if (ImGui.Selectable(w.ToString(), w == s.Waveform))
                    updated = updated with { Waveform = w };
            ImGui.EndCombo();
        }
        updated = updated with
        {
            Frequency = Slider("Fréquence de départ (Hz)", s.Frequency, 20, 4000, "%.0f", log: true),
            EndFrequency = Slider("Fréquence de fin (Hz)", s.EndFrequency, 20, 4000, "%.0f", log: true),
            Duration = Slider("Durée (s)", s.Duration, 0.02f, 5, "%.2f"),
            Attack = Slider("Attaque (s)", s.Attack, 0.001f, 1, "%.3f", log: true),
            Decay = Slider("Décroissance", s.Decay, 0.2f, 8, "%.1f"),
            Noise = Slider("Bruit", s.Noise, 0, 1, "%.2f"),
            LowPass = Slider("Filtre du bruit", s.LowPass, 0.01f, 1, "%.2f"),
            Drive = Slider("Saturation", s.Drive, 0, 1, "%.2f"),
        };
        if (updated != s)
            Replace(cue, cue with { Sound = new Sound { Synth = updated } });
        if (_waveFor is not Synth current || current != updated)
        {
            _wave = AudioClean.Envelope(Synthesizer.Render(updated, 8000), 240);
            _waveFor = updated;
        }
        ImGui.PlotLines("##onde"u8, _wave, 0, $"{updated.Duration:0.00} s", 0, 1, new float2(-1, 70));
        if (ImGui.Button("Écouter la synthèse"u8))
            _audio.Preview(updated, cue.Volume);
    }

    void DrawRecordings(Cue cue)
    {
        var variants = cue.Sound.Variants ?? (cue.Sound.File is { Length: > 0 } f ? [new SoundVariant { File = f }] : []);
        if (variants.Count == 0)
            ImGui.TextDisabled("Aucun enregistrement : cherchez sur Freesound, importez un fichier ou générez un son ci-dessous."u8);
        else if (ImGui.BeginTable("variants"u8, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Fichier"u8, ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Origine"u8, ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Licence"u8, ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{Path.GetFileName(v.File)}##v{i}", i == _variant, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                {
                    _variant = i;
                    _waveFor = null;
                }
                ImGui.TableNextColumn();
                ImGui.Text(v.Source is null ? "—" : $"{v.Source}{(v.Author is null ? "" : $" · {v.Author}")}");
                if (v.Url is not null)
                    ImGui.SetItemTooltip(v.Url);
                ImGui.TableNextColumn();
                ImGui.Text(v.License ?? "");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($">##p{i}"))
                    PlayFile(v.File, cue.Volume);
                ImGui.SetItemTooltip("Écouter cette variante"u8);
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##d{i}"))
                {
                    var list = variants.ToList();
                    list.RemoveAt(i);
                    Replace(cue, cue with { Sound = new Sound { Variants = list } });
                    _variant = 0;
                    _waveFor = null;
                    ImGui.EndTable();
                    return;
                }
                ImGui.SetItemTooltip("Retirer cette variante (le fichier est effacé à l'enregistrement)"u8);
            }
            ImGui.EndTable();
        }
        if (_variant < variants.Count)
        {
            string path = Path.Combine(_audio.ContentRoot, variants[_variant].File);
            if (!Equals(_waveFor, path))
            {
                _waveFor = path;
                try
                {
                    var samples = AudioEngine.Decode(path);
                    _wave = AudioClean.Envelope(samples, 240);
                    _waveSeconds = samples.Length / (float)Synthesizer.SampleRate;
                }
                catch (Exception)
                {
                    _wave = [];
                }
            }
            ImGui.PlotLines("##onde"u8, _wave, 0, $"{Path.GetFileName(path)} · {_waveSeconds:0.00} s", 0, 1, new float2(-1, 70));
        }

        ImGui.SeparatorText("Ajouter une variante"u8);
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("Durée max. (s)"u8, ref _maxSeconds, 0.3f, 10, "%.1f"u8);
        Widgets.HelpMarker("Le son importé est coupé à cette durée (avec un fondu), débarrassé du silence et ramené au volume des autres.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("Hauteur (×)"u8, ref _pitch, 0.4f, 1.6f, "%.2f"u8);
        Widgets.HelpMarker("Transpose le son à l'import, comme une bande ralentie : ×0,6 rend un blaster plus grave et plus long, un canon lourd.");
        if (ImGui.BeginTabBar("sources"u8))
        {
            if (ImGui.BeginTabItem("Freesound"u8))
            {
                DrawFreesound(cue);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Fichier"u8))
            {
                Widgets.TextField("Chemin", "sound-import-path", ref _importPath, 512);
                ImGui.TextDisabled("WAV, OGG, MP3 ou FLAC, n'importe où sur le disque."u8);
                ImGui.BeginDisabled(Busy || _importPath.Length == 0);
                if (ImGui.Button("Importer"u8))
                    ImportFile(cue.Id, _importPath, new SoundVariant { Source = Path.GetFileName(_importPath) });
                ImGui.EndDisabled();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("ElevenLabs"u8))
            {
                DrawElevenLabs(cue);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    float _waveSeconds;

    void PlayFile(string relative, float volume)
    {
        string path = Path.Combine(_audio.ContentRoot, relative);
        if (!File.Exists(path))
        {
            _log.Warn("son", $"Fichier introuvable : {relative}");
            return;
        }
        _audio.PreviewFile(path, Math.Clamp(volume, 0, 1));
    }

    void DrawFreesound(Cue cue)
    {
        if (_freesound is null)
        {
            ImGui.TextWrapped("Définissez FREESOUND_API_KEY (clé gratuite sur freesound.org/apiv2/apply) puis relancez l'éditeur."u8);
            return;
        }
        ImGui.SetNextItemWidth(-200);
        bool enter = Widgets.TextField("##query", "sound-freesound-query", ref _query, 256);
        ImGui.SameLine();
        ImGui.BeginDisabled(Busy || _query.Length == 0);
        if (ImGui.Button("Chercher"u8) || (enter && !Busy && _query.Length > 0))
            Search(_query);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("CC0 seulement"u8, ref _publicDomainOnly);
        ImGui.SetItemTooltip("Décoché : aussi les sons sous licence CC BY (l'auteur doit alors figurer dans les crédits du jeu)."u8);
        ImGui.TextDisabled("En anglais : « laser shot », « big explosion », « force field impact »…"u8);

        if (_results.Count == 0 || !ImGui.BeginTable("results"u8, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new float2(0, 220)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Son"u8, ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Auteur"u8, ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Durée"u8, ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Note"u8, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Licence"u8, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();
        foreach (var r in _results)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(r.Name);
            ImGui.SetItemTooltip(r.Url);
            ImGui.TableNextColumn();
            ImGui.Text(r.Author);
            ImGui.TableNextColumn();
            ImGui.Text($"{r.Duration:0.0} s");
            ImGui.TableNextColumn();
            ImGui.Text(r.Ratings > 0 ? $"{r.Rating:0.0} ({r.Ratings})" : "—");
            ImGui.TableNextColumn();
            ImGui.Text(r.LicenseName);
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(Busy);
            if (ImGui.SmallButton($"Écouter##l{r.Id}"))
            {
                var task = _freesound.DownloadPreviewAsync(r, _cacheDir);
                Run("Téléchargement", task, () => _audio.PreviewFile(task.Result, 0.8f));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"+##a{r.Id}"))
                ImportFreesound(cue.Id, r);
            ImGui.SetItemTooltip("Ajouter comme variante de cette cue"u8);
            ImGui.EndDisabled();
        }
        ImGui.EndTable();
    }

    void Search(string query)
    {
        var task = _freesound!.SearchAsync(query, _publicDomainOnly);
        Run("Recherche", task, () =>
        {
            _results = task.Result;
            if (_results.Count == 0)
                _log.Info("son", $"Freesound : rien pour « {query} ».");
        });
    }

    void ImportFreesound(string cueId, FreesoundResult r)
    {
        var task = Task.Run(async () =>
        {
            string path = await _freesound!.DownloadPreviewAsync(r, _cacheDir);
            return SoundImport.FromFile(_audio.ContentRoot, _bank!.Id, cueId, path, new SoundVariant
            {
                Source = $"Freesound {r.Id} « {r.Name} »{SoundImport.PitchNote(_pitch)}",
                Author = r.Author,
                License = r.LicenseName,
                Url = r.Url,
            }, _maxSeconds, _pitch);
        });
        Run("Import", task, () => AddVariant(cueId, task.Result));
    }

    void ImportFile(string cueId, string path, SoundVariant meta)
    {
        var task = Task.Run(() => SoundImport.FromFile(_audio.ContentRoot, _bank!.Id, cueId, path, meta with { Source = meta.Source + SoundImport.PitchNote(_pitch) }, _maxSeconds, _pitch));
        Run("Import", task, () => AddVariant(cueId, task.Result));
    }

    void DrawElevenLabs(Cue cue)
    {
        if (_elevenLabs is null)
        {
            ImGui.TextWrapped("Définissez ELEVENLABS_API_KEY puis relancez l'éditeur. La génération est payante (crédits ElevenLabs)."u8);
            return;
        }
        Widgets.TextField("Description", "sound-eleven-prompt", ref _prompt, 512);
        ImGui.TextDisabled("En anglais, précise : « heavy sci-fi plasma cannon shot, punchy, short tail »."u8);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderFloat("Durée (s)"u8, ref _genDuration, 0.5f, 10, "%.1f"u8);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.SliderFloat("Fidélité au texte"u8, ref _genInfluence, 0, 1, "%.2f"u8);
        ImGui.BeginDisabled(Busy || _prompt.Length == 0);
        if (ImGui.Button("Générer une variante"u8))
            Generate(cue.Id, _prompt, _genDuration, _genInfluence);
        ImGui.EndDisabled();
    }

    void Generate(string cueId, string prompt, float duration, float influence)
    {
        var task = Task.Run(async () =>
        {
            var samples = await _elevenLabs!.GenerateAsync(prompt, duration, influence);
            return SoundImport.Write(_audio.ContentRoot, _bank!.Id, cueId, samples, new SoundVariant
            {
                Source = $"ElevenLabs « {prompt} »{SoundImport.PitchNote(_pitch)}",
                License = "généré",
            }, _maxSeconds, _pitch);
        });
        Run("Génération", task, () => AddVariant(cueId, task.Result));
    }

    /// <summary>Ajoute la variante importée à la cue, enregistre la banque (le fichier est déjà écrit) et la fait entendre.</summary>
    void AddVariant(string cueId, SoundVariant? variant)
    {
        if (variant is null)
        {
            _log.Warn("son", "Le son importé n'est que du silence.");
            return;
        }
        int index = _bank!.Cues.FindIndex(c => c.Id == cueId);
        if (index < 0)
            return;
        var cue = _bank.Cues[index];
        var list = (cue.Sound.Variants ?? (cue.Sound.File is { Length: > 0 } f ? [new SoundVariant { File = f }] : [])).ToList();
        list.Add(variant);
        _bank.Cues[index] = cue with { Sound = new Sound { Variants = list } };
        _selected = index;
        _variant = list.Count - 1;
        _waveFor = null;
        Save();
        _log.Info("son", $"« {cueId} » : variante ajoutée ({variant.File}, {variant.Source}).");
        PlayFile(variant.File, cue.Volume);
    }

    /// <summary>Pour le script de l'éditeur : importe un son Freesound dans une cue de la banque ouverte, et attend.</summary>
    public void ImportFreesoundNow(string cueId, int soundId, float maxSeconds, float pitch = 1)
    {
        if (_freesound is null || _bank is null)
        {
            _log.Warn("son", "Freesound indisponible (FREESOUND_API_KEY) ou aucune banque.");
            return;
        }
        float previous = _maxSeconds;
        _maxSeconds = maxSeconds;
        try
        {
            var sound = _freesound.GetAsync(soundId).GetAwaiter().GetResult();
            string path = _freesound.DownloadPreviewAsync(sound, _cacheDir).GetAwaiter().GetResult();
            AddVariant(cueId, SoundImport.FromFile(_audio.ContentRoot, _bank.Id, cueId, path, new SoundVariant
            {
                Source = $"Freesound {sound.Id} « {sound.Name} »{SoundImport.PitchNote(pitch)}",
                Author = sound.Author,
                License = sound.LicenseName,
                Url = sound.Url,
            }, maxSeconds, pitch));
        }
        finally
        {
            _maxSeconds = previous;
        }
    }

    /// <summary>Pour le script : ouvre la cue (et cherche sur Freesound si une requête est donnée).</summary>
    public void Show(string cueId, string? query = null)
    {
        if (_bank is null && _audio.BankFiles().FirstOrDefault() is { } first)
            Open(first);
        int index = _bank?.Cues.FindIndex(c => c.Id == cueId) ?? -1;
        if (index >= 0)
            Select(index);
        if (query is { Length: > 0 } && _freesound is not null)
        {
            _query = query;
            Search(query);
        }
    }

    /// <summary>Vrai tant qu'une recherche, un import ou une génération est en cours.</summary>
    public bool Working => Busy;
}
