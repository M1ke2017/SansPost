namespace SansPost.Features.Music
{
    // Konfiguracja odkrywania stacji radiowych (sekcja "Music"). Bez sekretów — Radio Browser jest publicznym API.
    public sealed class MusicOptions
    {
        public const string Section = "Music";

        // Serwery Radio Browser (kolejne próby przy błędzie), rozdzielone przecinkami. "all" — rozsyłanie DNS po lustrach.
        public string RadioBrowserServers { get; set; } =
            "https://all.api.radio-browser.info,https://de1.api.radio-browser.info,https://de2.api.radio-browser.info";

        // API prosi o identyfikujący User-Agent.
        public string UserAgent { get; set; } = "SansPost/1.0 (portfolio demo; country radio discovery)";

        // Pojedyncze zapytanie do jednego serwera i cały budżet na wszystkie próby — radio nie może wstrzymać Saloonu.
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2.5);
        public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(5);

        // Świeża lista w pamięci; po awarii API — krótka przerwa przed kolejną próbą (bez czekania na timeout przy każdym wejściu).
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(20);
        public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);

        public IReadOnlyList<Uri> Servers => RadioBrowserServers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(server => Uri.TryCreate(server, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null)
            .OfType<Uri>()
            .ToList();
    }
}
