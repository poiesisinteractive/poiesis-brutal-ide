using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Poiesis.AI.Sounds;

/// <summary>Un son trouvé sur Freesound : de quoi le choisir (durée, note, licence) et l'écouter avant de l'importer.</summary>
public sealed record FreesoundResult(
    int Id, string Name, string Author, float Duration, float Rating, int Ratings, int Downloads, string License, string PreviewUrl, string Url)
{
    /// <summary>Nom court de la licence (« CC0 », « CC BY 4.0 »…), d'après son adresse.</summary>
    public string LicenseName => FreesoundClient.LicenseName(License);
}

/// <summary>
/// Recherche de sons sur <see href="https://freesound.org">Freesound</see> (API v2, clé dans <c>FREESOUND_API_KEY</c>).
/// Par défaut seulement le domaine public (CC0) et l'attribution (CC BY) : rien de « non commercial », un jeu se vend.
/// On télécharge l'aperçu haute qualité (OGG) ; l'original demande une connexion OAuth, inutile pour des effets.
/// </summary>
public sealed class FreesoundClient(string apiKey, HttpClient http)
{
    const string Api = "https://freesound.org/apiv2/";
    const string Fields = "id,name,username,duration,avg_rating,num_ratings,num_downloads,license,previews,url";

    public static FreesoundClient? FromEnvironment(HttpClient http) =>
        Environment.GetEnvironmentVariable("FREESOUND_API_KEY") is { Length: > 0 } key ? new FreesoundClient(key, http) : null;

    public async Task<List<FreesoundResult>> SearchAsync(string query, bool publicDomainOnly = true, float maxDuration = 8, int count = 30, CancellationToken ct = default)
    {
        string license = publicDomainOnly ? "license:\"Creative Commons 0\"" : "license:(\"Creative Commons 0\" OR \"Attribution\")";
        string filter = $"{license} duration:[0.05 TO {maxDuration.ToString(CultureInfo.InvariantCulture)}]";
        string url = $"{Api}search/text/?query={Uri.EscapeDataString(query)}&filter={Uri.EscapeDataString(filter)}&fields={Fields}&sort=score&page_size={count}";
        var json = await Get(url, ct);
        return json["results"]?.AsArray().Select(r => Parse(r!)).ToList() ?? [];
    }

    public async Task<FreesoundResult> GetAsync(int id, CancellationToken ct = default) =>
        Parse(await Get($"{Api}sounds/{id}/?fields={Fields}", ct));

    /// <summary>Télécharge l'aperçu dans <paramref name="cacheDir"/> (une fois), et rend le chemin du fichier.</summary>
    public async Task<string> DownloadPreviewAsync(FreesoundResult sound, string cacheDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        string path = Path.Combine(cacheDir, $"{sound.Id}.ogg");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;
        var bytes = await http.GetByteArrayAsync(sound.PreviewUrl, ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    async Task<JsonNode> Get(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
        using var response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Freesound : {(int)response.StatusCode} {Short(body)}");
        return JsonNode.Parse(body)!;
    }

    static FreesoundResult Parse(JsonNode r) => new(
        (int)r["id"]!,
        (string?)r["name"] ?? "",
        (string?)r["username"] ?? "",
        (float?)r["duration"] ?? 0,
        (float?)r["avg_rating"] ?? 0,
        (int?)r["num_ratings"] ?? 0,
        (int?)r["num_downloads"] ?? 0,
        (string?)r["license"] ?? "",
        (string?)r["previews"]?["preview-hq-ogg"] ?? "",
        (string?)r["url"] ?? "");

    public static string LicenseName(string url) =>
        url.Contains("publicdomain/zero", StringComparison.Ordinal) ? "CC0"
        : url.Contains("by-nc", StringComparison.Ordinal) ? "CC BY-NC"
        : url.Contains("licenses/by/4.0", StringComparison.Ordinal) ? "CC BY 4.0"
        : url.Contains("licenses/by/3.0", StringComparison.Ordinal) ? "CC BY 3.0"
        : url.Contains("sampling+", StringComparison.Ordinal) ? "Sampling+"
        : url;

    internal static string Short(string body) => body.Length > 200 ? body[..200] + "…" : body;
}

/// <summary>
/// Génération d'effets sonores par ElevenLabs (<c>/v1/sound-generation</c>, clé dans <c>ELEVENLABS_API_KEY</c>, envoyée
/// dans l'en-tête <c>xi-api-key</c>). Rend du PCM 16 bits mono à 44,1 kHz, converti en flottants.
/// </summary>
public sealed class ElevenLabsSounds(string apiKey, HttpClient http)
{
    public const int SampleRate = 44100;

    public static ElevenLabsSounds? FromEnvironment(HttpClient http) =>
        Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") is { Length: > 0 } key ? new ElevenLabsSounds(key, http) : null;

    /// <param name="prompt">Description du son, en anglais de préférence (« heavy sci-fi plasma cannon shot »).</param>
    /// <param name="duration">Durée voulue (0,5 à 30 s), ou null pour laisser le modèle choisir.</param>
    /// <param name="promptInfluence">0 : libre, 1 : colle à la description.</param>
    public async Task<float[]> GenerateAsync(string prompt, float? duration = null, float promptInfluence = 0.5f, CancellationToken ct = default)
    {
        var body = new JsonObject { ["text"] = prompt, ["prompt_influence"] = Math.Clamp(promptInfluence, 0, 1) };
        if (duration is { } d)
            body["duration_seconds"] = Math.Clamp(d, 0.5f, 30f);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/sound-generation?output_format=pcm_{SampleRate}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        using var response = await http.SendAsync(request, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs : {(int)response.StatusCode} {FreesoundClient.Short(System.Text.Encoding.UTF8.GetString(bytes))}");
        return Pcm16ToFloat(bytes);
    }

    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
        return samples;
    }
}
