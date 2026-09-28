using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features.Music;

namespace SansPost.Tests.Music
{
    // Sprint 18 — odkrywanie stacji country (MusicService + RadioBrowserClient) na podstawionym HTTP: wybór i sanityzacja,
    // pamięć podręczna, ostatnia poprawna lista, limity czasu, kolejne serwery. Bez sieci.
    [Trait("Category", "Music")]
    public class MusicServiceTests
    {
        internal sealed class FakeRadioBrowser : HttpMessageHandler
        {
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
                (_, _) => Task.FromResult(Json(Stations(3)));

            public List<HttpRequestMessage> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Requests)
                    Requests.Add(request);
                return Respond(request, cancellationToken);
            }

            public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            public static object Station(int i, string? url = null, string codec = "MP3", int bitrate = 128, int hls = 0, int ok = 1,
                string? homepage = null, string? name = null, string? uuid = null) => new Dictionary<string, object?>
            {
                ["changeuuid"] = Guid.NewGuid().ToString(),
                ["stationuuid"] = uuid ?? new Guid(i, 0, 0, new byte[8]).ToString(),
                ["name"] = name ?? $"Country Station {i}",
                ["url"] = $"http://example.com/{i}.pls",
                ["url_resolved"] = url ?? $"https://stream{i}.example.com/live.mp3",
                ["homepage"] = homepage ?? $"https://station{i}.example.com/",
                ["favicon"] = "https://example.com/f.ico",
                ["tags"] = "country",
                ["country"] = "The United States Of America",
                ["countrycode"] = "US",
                ["votes"] = 1000 - i,
                ["clickcount"] = 50,
                ["codec"] = codec,
                ["bitrate"] = bitrate,
                ["hls"] = hls,
                ["lastcheckok"] = ok,
                ["ssl_error"] = 0
            };

            public static object[] Stations(int count) => Enumerable.Range(1, count).Select(i => Station(i)).ToArray();
        }

        private sealed class TestClock : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        }

        private sealed class Factory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public Factory(HttpMessageHandler handler) => _handler = handler;

            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(new MusicOptions().UserAgent);
                return client;
            }
        }

        private static (MusicService Service, FakeRadioBrowser Api, TestClock Clock) Create(MusicOptions? options = null)
        {
            options ??= new MusicOptions { RadioBrowserServers = "https://one.example,https://two.example" };
            var api = new FakeRadioBrowser();
            var clock = new TestClock();
            var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
            var client = new RadioBrowserClient(new Factory(api), Options.Create(options), NullLogger<RadioBrowserClient>.Instance);
            return (new MusicService(client, cache, Options.Create(options), NullLogger<MusicService>.Instance), api, clock);
        }

        // 2 — lista: tylko sprawdzone, https, MP3/AAC, bez HLS, sensowny bitrate, bez powtórek; najwyżej 10 stacji.
        [Fact]
        public async Task Stations_AreFilteredSanitizedAndLimited()
        {
            var (service, api, _) = Create();
            var candidates = new List<object>
            {
                FakeRadioBrowser.Station(1),
                FakeRadioBrowser.Station(2, url: "http://insecure.example.com/live.mp3"),       // strumień bez https
                FakeRadioBrowser.Station(3, hls: 1),                                              // HLS
                FakeRadioBrowser.Station(4, ok: 0),                                               // ostatnie sprawdzenie nieudane
                FakeRadioBrowser.Station(5, codec: "OGG"),                                        // kodek spoza MP3/AAC
                FakeRadioBrowser.Station(6, bitrate: 24),                                         // za niski bitrate
                FakeRadioBrowser.Station(7, url: "https://stream1.example.com/live.mp3"),         // ten sam strumień
                FakeRadioBrowser.Station(8, name: "  Country   Station\u0007 1 "),                // ta sama nazwa po oczyszczeniu
                FakeRadioBrowser.Station(9, uuid: "not-a-guid"),
                FakeRadioBrowser.Station(10, homepage: "javascript:alert(1)", codec: "aac"),      // strona odrzucona, stacja zostaje
                FakeRadioBrowser.Station(11, url: "https://user:pass@stream11.example.com/x.mp3"),
                FakeRadioBrowser.Station(12, bitrate: 0, codec: "AAC+")                           // nieznany bitrate — dozwolony
            };
            candidates.AddRange(Enumerable.Range(20, 15).Select(i => FakeRadioBrowser.Station(i)));
            api.Respond = (_, _) => Task.FromResult(FakeRadioBrowser.Json(candidates));

            var result = await service.GetStationsAsync();

            Assert.True(result.Available);
            Assert.Equal(MusicStationsSource.Fresh, result.Source);
            Assert.Equal(MusicLimits.StationCount, result.Stations.Count);
            Assert.Equal(new[] { "Country Station 1", "Country Station 10", "Country Station 12" }, result.Stations.Take(3).Select(s => s.Name));
            Assert.All(result.Stations, s => Assert.StartsWith("https://", s.StreamUrl));
            Assert.All(result.Stations, s => Assert.True(Guid.TryParse(s.StationId, out _)));
            var aac = result.Stations[1];
            Assert.Equal(("AAC", 128, (string?)null), (aac.Codec, aac.Bitrate!.Value, aac.Homepage));
            Assert.Null(result.Stations[2].Bitrate);
            Assert.Equal("https://station1.example.com/", result.Stations[0].Homepage);
            Assert.Equal("The United States Of America", result.Stations[0].Country);

            // Zapytanie: tag country, hidebroken, https, kilkadziesiąt rekordów; identyfikujący User-Agent.
            var request = Assert.Single(api.Requests);
            var query = request.RequestUri!.Query;
            Assert.Equal("one.example", request.RequestUri.Host);
            foreach (var part in new[] { "tag=country", "hidebroken=true", "is_https=true", $"limit={MusicLimits.CandidateLimit}" })
                Assert.Contains(part, query);
            Assert.StartsWith("SansPost/", request.Headers.UserAgent.ToString());
        }

        // DTO: tylko pola potrzebne UI — bez głosów, kliknięć, faviconów, znaczników zmian.
        [Fact]
        public void Dto_ExposesOnlyUiFields()
        {
            Assert.Equal(
                new[] { "Bitrate", "Codec", "Country", "Homepage", "Name", "StationId", "StreamUrl" },
                typeof(RadioStationResponse).GetProperties().Select(p => p.Name).OrderBy(n => n));
        }

        // 3 — pamięć podręczna: kolejne wejścia bez zapytania do API; po wygaśnięciu — nowe zapytanie.
        [Fact]
        public async Task Cache_HitWithinDuration_MissAfterExpiry()
        {
            var (service, api, clock) = Create();

            Assert.Equal(MusicStationsSource.Fresh, (await service.GetStationsAsync()).Source);
            Assert.Equal(MusicStationsSource.Cache, (await service.GetStationsAsync()).Source);
            clock.UtcNow += TimeSpan.FromMinutes(19);
            Assert.Equal(MusicStationsSource.Cache, (await service.GetStationsAsync()).Source);
            Assert.Single(api.Requests);

            clock.UtcNow += TimeSpan.FromMinutes(2);
            Assert.Equal(MusicStationsSource.Fresh, (await service.GetStationsAsync()).Source);
            Assert.Equal(2, api.Requests.Count);
        }

        // Równoległe wejścia przy pustej pamięci — jedno zapytanie do API.
        [Fact]
        public async Task ConcurrentFirstRequests_QueryApiOnce()
        {
            var (service, api, _) = Create();
            var release = new TaskCompletionSource();
            api.Respond = async (_, _) => { await release.Task; return FakeRadioBrowser.Json(FakeRadioBrowser.Stations(3)); };

            var calls = Enumerable.Range(0, 5).Select(_ => service.GetStationsAsync()).ToList();
            release.SetResult();
            var results = await Task.WhenAll(calls);

            Assert.Single(api.Requests);
            Assert.All(results, r => Assert.Equal(3, r.Stations.Count));
        }

        // 4 — API niedostępne bez wcześniejszej listy: "chwilowo niedostępne", bez wyjątku; krótka przerwa przed kolejną próbą.
        [Fact]
        public async Task ApiUnavailable_WithoutCache_ReportsUnavailable_AndBacksOff()
        {
            var (service, api, clock) = Create();
            api.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var result = await service.GetStationsAsync();

            Assert.False(result.Available);
            Assert.Equal(MusicStationsSource.Unavailable, result.Source);
            Assert.Empty(result.Stations);
            Assert.Equal(2, api.Requests.Count);                       // oba serwery spróbowane

            Assert.False((await service.GetStationsAsync()).Available);  // w przerwie — bez kolejnych zapytań
            Assert.Equal(2, api.Requests.Count);

            clock.UtcNow += TimeSpan.FromSeconds(31);
            api.Respond = (_, _) => Task.FromResult(FakeRadioBrowser.Json(FakeRadioBrowser.Stations(2)));
            Assert.Equal(MusicStationsSource.Fresh, (await service.GetStationsAsync()).Source);
        }

        // 4 — API niedostępne po wygaśnięciu pamięci: ostatnia poprawna lista.
        [Fact]
        public async Task ApiUnavailable_AfterSuccess_ServesLastKnownGoodList()
        {
            var (service, api, clock) = Create();
            var first = await service.GetStationsAsync();
            clock.UtcNow += TimeSpan.FromMinutes(21);
            api.Respond = (_, _) => throw new HttpRequestException("down");

            var result = await service.GetStationsAsync();

            Assert.True(result.Available);
            Assert.Equal(MusicStationsSource.LastKnownGood, result.Source);
            Assert.Equal(first.Stations, result.Stations);
        }

        // Odpowiedź, z której nic nie przechodzi filtra (albo nie-JSON), liczy się jak niedostępność.
        [Theory]
        [InlineData("[]")]
        [InlineData("<html>maintenance</html>")]
        public async Task EmptyOrInvalidResponse_IsUnavailable(string body)
        {
            var (service, api, _) = Create();
            api.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

            Assert.False((await service.GetStationsAsync()).Available);
        }

        // Limit czasu: zawieszony serwer kosztuje RequestTimeout, potem kolejny serwer; całość w TotalTimeout.
        [Fact]
        public async Task HangingServer_TimesOut_NextServerAnswers()
        {
            var (service, api, _) = Create(new MusicOptions
            {
                RadioBrowserServers = "https://hang.example,https://ok.example",
                RequestTimeout = TimeSpan.FromMilliseconds(300),
                TotalTimeout = TimeSpan.FromSeconds(2)
            });
            api.Respond = async (request, token) =>
            {
                if (request.RequestUri!.Host == "hang.example")
                    await Task.Delay(Timeout.Infinite, token);
                return FakeRadioBrowser.Json(FakeRadioBrowser.Stations(2));
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.GetStationsAsync();

            Assert.True(result.Available);
            Assert.Equal(new[] { "hang.example", "ok.example" }, api.Requests.Select(r => r.RequestUri!.Host));
            Assert.InRange(watch.ElapsedMilliseconds, 250, 1500);
        }

        [Fact]
        public async Task AllServersHanging_GiveUpWithinTotalTimeout()
        {
            var (service, api, _) = Create(new MusicOptions
            {
                RadioBrowserServers = "https://a.example,https://b.example,https://c.example",
                RequestTimeout = TimeSpan.FromMilliseconds(400),
                TotalTimeout = TimeSpan.FromMilliseconds(700)
            });
            api.Respond = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.GetStationsAsync();

            Assert.False(result.Available);
            Assert.InRange(watch.ElapsedMilliseconds, 600, 1500);
            Assert.Equal(2, api.Requests.Count);   // trzeci serwer nie zmieścił się w budżecie
        }
    }
}
