using SansPost.Features.Identity;

namespace SansPost.Shared.Ui
{
    // Komunikaty logowania i rejestracji — wspólne dla stron /login, /register i kart na Entrance.
    // Kody błędów pochodzą z przekierowań AccountController (?error=…).
    public static class AuthText
    {
        public const string InvalidCredentials = "Nieprawidłowy email lub hasło.";
        public const string LoginRateLimited = "Zbyt wiele prób logowania. Odczekaj chwilę i spróbuj ponownie.";
        public const string RegisterRateLimited = "Zbyt wiele prób w krótkim czasie. Odczekaj chwilę i spróbuj ponownie.";
        public const string PasswordMismatch = "Hasła nie są identyczne. Wpisz je ponownie.";
        public const string IdentityTaken = "Nie można utworzyć konta z tym adresem email. Użyj innego.";
        public const string AliasTaken = "Ten przydomek został właśnie zajęty. Wylosowaliśmy dla Ciebie nowy.";
        public const string Unavailable = "Nie udało się połączyć z Saloonem. Spróbuj ponownie za chwilę.";

        public static string InvalidRegistration =>
            $"Sprawdź dane: poprawny email i hasło {PasswordPolicy.MinLength}–{PasswordPolicy.MaxLength} znaków.";
    }
}
