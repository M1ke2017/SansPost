using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SansPost.Features.Music
{
    // Lista stacji country dla Kącika muzycznego: Radio Browser → wybór i sanityzacja → pamięć podręczna procesu.
    //   świeża lista: MemoryCache na CacheDuration (wejście do strefy nie pyta zewnętrznego API),
    //   awaria API: ostatnia poprawna lista (LastKnownGood), a bez niej "radio chwilowo niedostępne";
    //   po awarii krótka przerwa (FailureBackoff) — kolejne wejścia nie czekają na ten sam timeout.
    // Jedna próba naraz (semafor): równoległe wejścia przy pustej pamięci nie mnożą zapytań do API.
    public sealed class MusicService
    {
        private const string FreshKey = "music:stations";
        private const string BackoffKey = "music:stations:backoff";

        private readonly RadioBrowserClient _client;
        private readonly IMemoryCache _cache;
        private readonly MusicOptions _options;
        private readonly ILogger<MusicService> _logger;
        private readonly SemaphoreSlim _refresh = new(1, 1);
        private IReadOnlyList<RadioStationResponse>? _lastKnownGood;

        public MusicService(RadioBrowserClient client, IMemoryCache cache, IOptions<MusicOptions> options, ILogger<MusicService> logger)
        {
            _client = client;
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<MusicStationsResult> GetStationsAsync(CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(FreshKey, out IReadOnlyList<RadioStationResponse>? cached) && cached is not null)
                return new MusicStationsResult(cached, true, MusicStationsSource.Cache);

            await _refresh.WaitAsync(cancellationToken);
            try
            {
                if (_cache.TryGetValue(FreshKey, out cached) && cached is not null)
                    return new MusicStationsResult(cached, true, MusicStationsSource.Cache);
                if (_cache.TryGetValue(BackoffKey, out _))
                    return Fallback();

                try
                {
                    var stations = Select(await _client.SearchCountryAsync(cancellationToken));
                    if (stations.Count == 0)
                        throw new RadioBrowserUnavailableException();

                    _cache.Set(FreshKey, stations, _options.CacheDuration);
                    _lastKnownGood = stations;
                    return new MusicStationsResult(stations, true, MusicStationsSource.Fresh);
                }
                catch (RadioBrowserUnavailableException)
                {
                    _logger.LogWarning("Lista stacji niedostępna — {Fallback}.", _lastKnownGood is null ? "brak ostatniej listy" : "ostatnia poprawna lista");
                    _cache.Set(BackoffKey, true, _options.FailureBackoff);
                    return Fallback();
                }
            }
            finally
            {
                _refresh.Release();
            }
        }

        private MusicStationsResult Fallback() => _lastKnownGood is { } stations
            ? new MusicStationsResult(stations, true, MusicStationsSource.LastKnownGood)
            : new MusicStationsResult(Array.Empty<RadioStationResponse>(), false, MusicStationsSource.Unavailable);

        // Wybór: sprawdzone (lastcheckok), bez HLS (element audio przeglądarki gra je tylko w części przeglądarek),
        // strumień https, MP3 albo AAC, rozsądny bitrate; bez powtórek (ten sam strumień albo nazwa). Kolejność z API
        // (najwięcej głosów słuchaczy) zostaje.
        public static IReadOnlyList<RadioStationResponse> Select(IEnumerable<RadioBrowserStation> candidates)
        {
            var seenStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<RadioStationResponse>();

            foreach (var station in candidates)
            {
                if (result.Count == MusicLimits.StationCount)
                    break;
                if (station.Hls != 0 || station.LastCheckOk != 1)
                    continue;
                if (!Guid.TryParse(station.StationUuid, out var id))
                    continue;
                if (SafeUrl(station.UrlResolved, httpsOnly: true) is not { } stream)
                    continue;
                var codec = (station.Codec ?? "").Trim().ToUpperInvariant();
                if (codec is not ("MP3" or "AAC" or "AAC+"))
                    continue;
                if (station.Bitrate != 0 && station.Bitrate is < MusicLimits.MinBitrate or > MusicLimits.MaxBitrate)
                    continue;
                var name = Clean(station.Name, MusicLimits.NameMaxLength);
                if (name is null || !seenStreams.Add(stream) || !seenNames.Add(name))
                    continue;

                result.Add(new RadioStationResponse(
                    id.ToString("D"),
                    name,
                    Clean(station.Country, MusicLimits.CountryMaxLength),
                    SafeUrl(station.Homepage, httpsOnly: false),
                    stream,
                    codec,
                    station.Bitrate > 0 ? station.Bitrate : null));
            }

            return result;
        }

        // Tylko bezwzględny adres http(s) z hostem, bez danych logowania w adresie; strumień — wyłącznie https.
        private static string? SafeUrl(string? value, bool httpsOnly)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
                return null;
            var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp);
            return schemeOk && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
        }

        // Tekst z zewnętrznego API: bez znaków sterujących, ściśnięte spacje, przycięty do limitu.
        private static string? Clean(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                if (char.IsControl(ch))
                    continue;
                if (char.IsWhiteSpace(ch) && builder.Length > 0 && builder[^1] == ' ')
                    continue;
                builder.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            }
            var text = builder.ToString().Trim();
            return text.Length == 0 ? null : text.Length > maxLength ? text[..maxLength].TrimEnd() + "…" : text;
        }
    }
}
