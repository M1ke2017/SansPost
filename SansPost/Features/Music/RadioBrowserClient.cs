using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SansPost.Features.Music
{
    // Klient Radio Browser (https://api.radio-browser.info): wyszukiwanie działających stacji country. Tylko discovery —
    // strumienie audio odtwarza przeglądarka bezpośrednio ze stacji (serwer SansPost nie przekazuje audio).
    // Krótki timeout na serwer, kolejne serwery przy błędzie, łączny budżet czasu; wyjątek = radio niedostępne.
    // HttpClient z IHttpClientFactory przy każdym wyszukiwaniu (nazwany klient "RadioBrowser": User-Agent, limit
    // odpowiedzi) — singleton nie trzyma jednego HttpClient na zawsze (rotacja połączeń i DNS).
    public sealed class RadioBrowserClient
    {
        public const string HttpClientName = "RadioBrowser";

        private readonly IHttpClientFactory _httpFactory;
        private readonly MusicOptions _options;
        private readonly ILogger<RadioBrowserClient> _logger;

        public RadioBrowserClient(IHttpClientFactory httpFactory, IOptions<MusicOptions> options, ILogger<RadioBrowserClient> logger)
        {
            _httpFactory = httpFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<RadioBrowserStation>> SearchCountryAsync(CancellationToken cancellationToken)
        {
            // tag=country, tylko sprawdzone (hidebroken) i https; najpopularniejsze najpierw; kilkadziesiąt rekordów, nie setki.
            var query = $"json/stations/search?tag=country&hidebroken=true&is_https=true&order=votes&reverse=true&limit={MusicLimits.CandidateLimit}";

            var http = _httpFactory.CreateClient(HttpClientName);
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            total.CancelAfter(_options.TotalTimeout);

            foreach (var server in _options.Servers)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                attempt.CancelAfter(_options.RequestTimeout);
                try
                {
                    var stations = await http.GetFromJsonAsync<List<RadioBrowserStation>>(new Uri(server, query), attempt.Token);
                    return stations ?? new List<RadioBrowserStation>();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or NotSupportedException)
                {
                    _logger.LogWarning("Radio Browser {Server} nie odpowiedział poprawnie: {Error}", server.Host, ex.GetType().Name);
                    if (total.IsCancellationRequested)
                        break;
                }
            }

            throw new RadioBrowserUnavailableException();
        }
    }

    public sealed class RadioBrowserUnavailableException : Exception
    {
        public RadioBrowserUnavailableException() : base("Radio Browser jest niedostępny.")
        {
        }
    }

    // Rekord Radio Browser — tylko pola potrzebne do wyboru i mapowania (reszta odpowiedzi jest pomijana).
    public sealed class RadioBrowserStation
    {
        [JsonPropertyName("stationuuid")] public string? StationUuid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url_resolved")] public string? UrlResolved { get; set; }
        [JsonPropertyName("homepage")] public string? Homepage { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("codec")] public string? Codec { get; set; }
        [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
        [JsonPropertyName("hls")] public int Hls { get; set; }
        [JsonPropertyName("lastcheckok")] public int LastCheckOk { get; set; } = 1;
    }
}
