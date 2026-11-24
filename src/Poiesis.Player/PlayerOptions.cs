using Poiesis.Engine.Scene;

namespace Poiesis.Player;

/// <summary>Les options du joueur (volumes, bloom, aide), gardées dans <c>user/options.json</c> à côté du jeu.</summary>
public sealed record PlayerOptions
{
    public float MasterVolume { get; init; } = 0.8f;
    public float EffectsVolume { get; init; } = 1f;
    public float Bloom { get; init; } = 0.5f;
    public bool ShowHelp { get; init; } = true;

    public static PlayerOptions Load(string path)
    {
        try
        {
            return File.Exists(path) ? ValueCodec.Read<PlayerOptions>(File.ReadAllText(path)) : new PlayerOptions();
        }
        catch (Exception)
        {
            return new PlayerOptions();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ValueCodec.ToJson(this, typeof(PlayerOptions))!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
