using SansPost.Infrastructure.Hosting;

namespace SansPost.Infrastructure.Security
{
    // Zdarzenia bezpieczeństwa w logu operatora. Nigdy: email, hasło, token, cookie — tylko kanał i adres klienta
    // (za zaufanym proxy: prawdziwy adres z X-Forwarded-For).
    public static class SecurityEvents
    {
        public static void FailedLogin(HttpContext? http, string channel)
        {
            SansPostTelemetry.FailedLogins.Add(1, new KeyValuePair<string, object?>("channel", channel));
            http?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("SansPost.Security")
                .LogWarning("Failed login via {Channel} from {ClientIp}.", channel, http.Connection.RemoteIpAddress?.ToString());
        }
    }
}
