using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SansPost.Infrastructure.Security
{
    // Nagłówki bezpieczeństwa (Sprint 25) — dla każdej odpowiedzi, także błędów (OnStarting, jak X-Request-Id):
    //   • CSP bez 'unsafe-eval': skrypty tylko z własnego origin + nonce dla dwóch skryptów inline w _Host (motyw, wczesny
    //     start sceny) + hashe skryptów statycznej strony błędu; brak osadzania w ramkach (frame-ancestors 'none').
    //     style-src z 'unsafe-inline' — komponenty używają atrybutów style (świadomy wyjątek, bez wpływu na skrypty).
    //     media-src https: — radio gra przeglądarka bezpośrednio ze stacji (wyłącznie strumienie https, MusicService).
    //     connect-src: ten sam origin, także WebSocket (Blazor, stół gry SignalR) — jawnie ws(s)://host dla starszych Safari.
    //   • nosniff, Referrer-Policy, X-Frame-Options (starsze przeglądarki), Permissions-Policy bez kamery/mikrofonu/geolokacji.
    // HSTS ustawia UseHsts (poza Development), HTTPS kończy się na reverse proxy.
    public static class SecurityHeaders
    {
        private const string NonceItem = "SansPost.CspNonce";

        public static string Nonce(HttpContext context) => context.Items[NonceItem] as string ?? "";

        public static IApplicationBuilder UseSansPostSecurityHeaders(this IApplicationBuilder app, IWebHostEnvironment environment)
        {
            var errorPageHashes = InlineScriptHashes(Path.Combine(environment.WebRootPath ?? "", "error.html"));
            return app.Use((context, next) =>
            {
                // Hex, nie base64: Razor koduje w atrybucie "+", "/" i "=" (np. &#x2B;), a wtedy nonce w HTML nie pasuje do nagłówka.
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                context.Items[NonceItem] = nonce;
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.ContentSecurityPolicy = Policy(context.Request.Host, nonce, errorPageHashes);
                    headers.XContentTypeOptions = "nosniff";
                    headers.XFrameOptions = "DENY";
                    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                    return Task.CompletedTask;
                });
                return next(context);
            });
        }

        public static string Policy(HostString host, string nonce, IReadOnlyList<string> scriptHashes)
        {
            var sockets = host.HasValue ? $" ws://{host.Value} wss://{host.Value}" : "";
            var hashes = string.Concat(scriptHashes.Select(h => $" '{h}'"));
            return string.Join("; ",
                "default-src 'self'",
                $"script-src 'self' 'nonce-{nonce}'{hashes}",
                "style-src 'self' 'unsafe-inline'",
                "img-src 'self' data:",
                "font-src 'self' data:",
                "media-src 'self' https:",
                $"connect-src 'self'{sockets}",
                "object-src 'none'",
                "base-uri 'self'",
                "form-action 'self'",
                "frame-ancestors 'none'");
        }

        // Hashe (sha256) skryptów inline statycznej strony błędu — liczone z pliku, który faktycznie jest serwowany.
        public static IReadOnlyList<string> InlineScriptHashes(string htmlPath)
        {
            if (!File.Exists(htmlPath))
                return Array.Empty<string>();
            var html = File.ReadAllText(htmlPath);
            return Regex.Matches(html, @"<script>(.*?)</script>", RegexOptions.Singleline)
                .Select(m => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))))
                .ToList();
        }
    }
}
