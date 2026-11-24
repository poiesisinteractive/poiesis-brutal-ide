using System.Text.Encodings.Web;
using System.Text.Json;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Audio;
using Poiesis.Engine.Audio;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Le panneau « Sons » : les banques de sons du jeu (<c>content/audio/*.cues.json</c>), cue par cue. On règle la synthèse
/// (forme d'onde, glissement de fréquence, bruit, enveloppe, saturation) ou le fichier, les voix et la portée ; on voit
/// la forme d'onde, on écoute, on enregistre (les sons sont refaits aussitôt).
/// </summary>
sealed class SoundPanel(AudioEngine audio, LogBuffer log)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    string? _file;
    CueBank? _bank;
    int _selected;
    bool _dirty;
    float[] _wave = [];
    Synth? _waveFor;

    public void Draw()
    {
        if (ImGui.Begin("Sons"u8))
            DrawContent();
        ImGui.End();
    }

    void DrawContent()
    {
        if (!audio.Available)
            ImGui.TextColored(EditorTheme.Warning, "Son indisponible : les banques s'éditent, mais rien ne s'entend."u8);
        else
            ImGui.TextDisabled($"Pilote audio : {audio.Driver} · {audio.CueIds.Count} cues prêtes · {audio.Played} sons joués");

        float master = audio.MasterVolume;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Volume général"u8, ref master, 0, 1, "%.2f"u8))
            audio.MasterVolume = master;
        ImGui.SameLine();
        bool muted = audio.Muted;
        if (ImGui.Checkbox("Muet en jeu"u8, ref muted))
            audio.Muted = muted;

        var files = audio.BankFiles().ToList();
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

        if (ImGui.BeginChild("cues"u8, new float2(170, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX))
        {
            for (int i = 0; i < _bank.Cues.Count; i++)
            {
                if (ImGui.Selectable($"{_bank.Cues[i].Id}##{i}", i == _selected))
                    _selected = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    Listen(_bank.Cues[i]);
            }
            if (ImGui.SmallButton("+ Cue"u8))
            {
                _bank.Cues.Add(new Cue { Id = $"son{_bank.Cues.Count + 1}", Sound = new Sound { Synth = new Synth() } });
                _selected = _bank.Cues.Count - 1;
                _dirty = true;
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("cue"u8) && _selected < _bank.Cues.Count)
            DrawCue(_bank.Cues[_selected]);
        ImGui.EndChild();
    }

    void Open(string file)
    {
        try
        {
            _bank = ValueCodec.Read<CueBank>(File.ReadAllText(file));
            _file = file;
            _selected = 0;
            _dirty = false;
        }
        catch (Exception ex)
        {
            log.Error("son", $"{Path.GetFileName(file)} : {ex.Message}");
        }
    }

    void Save()
    {
        File.WriteAllText(_file!, ValueCodec.ToJson(_bank, typeof(CueBank))!.ToJsonString(Json) + "\n");
        _dirty = false;
        audio.ReloadBanks();
        log.Info("son", $"Banque enregistrée : {Path.GetFileName(_file)} ({_bank!.Cues.Count} cues), sons refaits.");
    }

    void Listen(Cue cue)
    {
        if (cue.Sound.Synth is { } synth)
            audio.Preview(synth, cue.Volume);
        else
            audio.Preview(cue.Id);
    }

    void Replace(Cue old, Cue updated)
    {
        if (old == updated)
            return;
        _bank!.Cues[_selected] = updated;
        _dirty = true;
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

        ImGui.SeparatorText("Son"u8);
        bool synthMode = cue.Sound.Synth is not null;
        if (ImGui.RadioButton("Synthèse"u8, synthMode) && !synthMode)
            cue = Updated(cue, cue with { Sound = new Sound { Synth = new Synth() } });
        ImGui.SameLine();
        if (ImGui.RadioButton("Fichier"u8, !synthMode) && synthMode)
            cue = Updated(cue, cue with { Sound = new Sound { File = "audio/" } });

        if (cue.Sound.Synth is { } s)
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
                cue = Updated(cue, cue with { Sound = new Sound { Synth = updated } });
            DrawWave(updated);
        }
        else
        {
            string file = cue.Sound.File ?? "";
            if (Widgets.TextField("Fichier (dans content/)", $"cue-file-{_selected}", ref file))
                cue = Updated(cue, cue with { Sound = new Sound { File = file } });
            ImGui.TextDisabled("WAV, OGG, MP3 ou FLAC ; enregistrez pour l'entendre."u8);
        }

        ImGui.Spacing();
        if (ImGui.Button("Écouter"u8, new float2(100, 0)))
            Listen(cue);
        ImGui.SameLine();
        if (ImGui.Button("Supprimer la cue"u8))
        {
            _bank.Cues.RemoveAt(_selected);
            _selected = Math.Max(0, _selected - 1);
            _dirty = true;
        }
    }

    Cue Updated(Cue old, Cue updated)
    {
        Replace(old, updated);
        return updated;
    }

    /// <summary>La forme d'onde (enveloppe : maximum par tranche), refaite quand la synthèse change.</summary>
    void DrawWave(Synth synth)
    {
        if (_waveFor != synth)
        {
            var samples = Synthesizer.Render(synth, 8000);
            int buckets = 240;
            _wave = new float[buckets];
            for (int b = 0; b < buckets; b++)
            {
                int from = b * samples.Length / buckets, to = Math.Max(from + 1, (b + 1) * samples.Length / buckets);
                float m = 0;
                for (int i = from; i < to && i < samples.Length; i++)
                    m = MathF.Max(m, MathF.Abs(samples[i]));
                _wave[b] = m;
            }
            _waveFor = synth;
        }
        ImGui.PlotLines("##onde"u8, _wave, 0, $"{synth.Duration:0.00} s", 0, 1, new float2(-1, 70));
    }
}
