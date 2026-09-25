using System.Diagnostics.Metrics;

namespace SansPost.Infrastructure.Hosting
{
    // Lekkie metryki aplikacji (System.Diagnostics.Metrics). Liczba i czas żądań HTTP oraz 5xx pochodzą z wbudowanego
    // metera "Microsoft.AspNetCore.Hosting" (http.server.request.duration z http.response.status_code).
    // Odczyt: dotnet-counters / dowolny eksporter OpenTelemetry — bez eksportera w samym obrazie.
    public static class SansPostTelemetry
    {
        public const string MeterName = "SansPost";

        public static readonly Meter Meter = new(MeterName, "1.0");

        // policy: Auth | Search | Writes | AliasSuggestion
        public static readonly Counter<long> RateLimitRejections =
            Meter.CreateCounter<long>("sanspost.rate_limit.rejections", description: "Żądania odrzucone przez limity (REST i Blazor).");

        public static readonly Counter<long> NotificationsCreated =
            Meter.CreateCounter<long>("sanspost.notifications.created", description: "Powiadomienia dodane (zapisywane razem z komentarzem).");

        // action: HidePost, BanUser, …
        public static readonly Counter<long> ModerationActions =
            Meter.CreateCounter<long>("sanspost.moderation.actions", description: "Działania moderacyjne zapisane w dzienniku.");

        // source: readiness | request
        public static readonly Counter<long> DatabaseFailures =
            Meter.CreateCounter<long>("sanspost.db.failures", description: "Niedostępność bazy wykryta przez readiness lub nieobsłużony błąd bazy w żądaniu.");

        public static readonly Counter<long> FailedLogins =
            Meter.CreateCounter<long>("sanspost.auth.failed_logins", description: "Nieudane logowania (formularz i REST).");
    }
}
