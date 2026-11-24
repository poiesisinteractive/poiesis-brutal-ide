using System.Net;
using System.Text;
using Poiesis.AI.Sounds;

namespace Poiesis.AI.Tests;

public class SoundSourceTests
{
    sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    [Fact]
    public async Task Freesound_search_sends_the_token_filters_licences_and_reads_results()
    {
        const string json = """
            {"count":1,"results":[{"id":787866,"name":"Force Field Knock","username":"modusmogulus","duration":2.27,
             "avg_rating":5.0,"num_ratings":10,"num_downloads":202,"license":"http://creativecommons.org/publicdomain/zero/1.0/",
             "previews":{"preview-hq-ogg":"https://cdn.freesound.org/previews/787/787866.ogg"},"url":"https://freesound.org/people/modusmogulus/sounds/787866/"}]}
            """;
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var client = new FreesoundClient("secret", new HttpClient(server));

        var results = await client.SearchAsync("force field", publicDomainOnly: true);

        var r = Assert.Single(results);
        Assert.Equal((787866, "modusmogulus", "CC0"), (r.Id, r.Author, r.LicenseName));
        Assert.EndsWith("787866.ogg", r.PreviewUrl);
        var (request, _) = Assert.Single(server.Seen);
        Assert.Equal("Token secret", request.Headers.GetValues("Authorization").Single());
        string query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.Contains("query=force field", query);
        Assert.Contains("license:\"Creative Commons 0\"", query);
        Assert.Equal("CC BY 4.0", FreesoundClient.LicenseName("https://creativecommons.org/licenses/by/4.0/"));
        Assert.Equal("CC BY-NC", FreesoundClient.LicenseName("http://creativecommons.org/licenses/by-nc/3.0/"));
    }

    [Fact]
    public async Task ElevenLabs_generation_uses_the_xi_api_key_header_and_returns_pcm_samples()
    {
        var pcm = new byte[] { 0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F };   // 0,5 ; -0,5 ; presque 1
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) });
        var client = new ElevenLabsSounds("secret", new HttpClient(server));

        var samples = await client.GenerateAsync("heavy plasma cannon shot", duration: 1.5f, promptInfluence: 0.7f);

        Assert.Equal([0.5f, -0.5f, 32767 / 32768f], samples);
        var (request, body) = Assert.Single(server.Seen);
        Assert.Equal("secret", request.Headers.GetValues("xi-api-key").Single());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Contains("output_format=pcm_44100", request.RequestUri!.Query);
        Assert.Contains("\"text\":\"heavy plasma cannon shot\"", body);
        Assert.Contains("\"duration_seconds\":1.5", body);
    }

    [Fact]
    public async Task A_refused_generation_reports_the_status()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"invalid key\"}") });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new ElevenLabsSounds("bad", new HttpClient(server)).GenerateAsync("boom"));
        Assert.Contains("401", ex.Message);
    }
}
