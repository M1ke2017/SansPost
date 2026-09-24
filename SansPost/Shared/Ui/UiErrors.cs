using SansPost.Features;

namespace SansPost.Shared.Ui
{
    // Mapowanie wyników serwisów na komunikaty produktowe. Semantyka backendu (404/409/412/429) zostaje bez zmian —
    // zmienia się tylko to, co widzi człowiek: żadnych nazw wyjątków, kodów HTTP ani zrzutów ProblemDetails.
    public static class UiErrors
    {
        public const string Generic = "Coś poszło nie tak. Spróbuj ponownie za chwilę.";
        public const string NotAvailable = "Ta treść nie jest już dostępna.";
        public const string Suspended = "Twoje konto jest zawieszone — możesz czytać, ale nie możesz publikować, komentować ani reagować.";
        public const string SessionEnded = "Twoja sesja wygasła. Zaloguj się ponownie.";
        public const string StaleContent = "Ta treść została zmieniona w innym miejscu. Odśwież aktualną wersję.";
        public const string NoPermission = "Nie masz uprawnień do tej operacji.";

        public static string Describe(ServiceResult result, string? notFound = null, string? stale = null) => result.Error switch
        {
            ServiceError.Validation => result.Message ?? "Sprawdź wprowadzone dane.",
            ServiceError.NotFound => notFound ?? NotAvailable,
            ServiceError.Forbidden => result.Code switch
            {
                "account-suspended" => Suspended,
                "account-inactive" => SessionEnded,
                _ => result.Message ?? NoPermission
            },
            ServiceError.Conflict => result.Message ?? "Operacja koliduje z aktualnym stanem. Odśwież stronę.",
            ServiceError.PreconditionFailed => stale ?? StaleContent,
            ServiceError.RateLimited => RateLimited(result.RetryAfter),
            _ => Generic
        };

        public static string RateLimited(TimeSpan? retryAfter) => retryAfter is { } wait && wait > TimeSpan.Zero
            ? $"Zbyt wiele operacji w krótkim czasie. Spróbuj ponownie za {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s."
            : "Zbyt wiele operacji w krótkim czasie. Spróbuj ponownie za chwilę.";

        // Ostrzeżenie (żółte) dla sytuacji przejściowych, błąd (czerwone) dla pozostałych.
        public static AlertVariant Variant(ServiceResult result) => result.Error switch
        {
            ServiceError.PreconditionFailed or ServiceError.RateLimited or ServiceError.Conflict => AlertVariant.Warning,
            ServiceError.Forbidden when result.Code == "account-suspended" => AlertVariant.Warning,
            _ => AlertVariant.Danger
        };

        // Konto zablokowane / sesja nieaktualna — UI powinien przeładować stronę, aby cookie zostało zweryfikowane.
        public static bool EndsSession(ServiceResult result) =>
            result.Error == ServiceError.Forbidden && result.Code == "account-inactive";
    }

    public enum AlertVariant
    {
        Info,
        Success,
        Warning,
        Danger
    }
}
