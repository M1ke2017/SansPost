namespace SansPost.Features.Identity
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string NormalizedUsername { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string NormalizedEmail { get; set; } = string.Empty;

        // Nigdy nie opuszcza modułu Identity (brak w DTO i claims).
        public string PasswordHash { get; set; } = string.Empty;

        // Uprawnienia (co wolno). Status konta to osobny koncept (czy konto może działać).
        public UserRole Role { get; set; } = UserRole.User;
        public AccountStatus Status { get; set; } = AccountStatus.Active;

        // Security stamp: zwiększany przy zmianie roli/statusu. Cookie i JWT niosą wersję z chwili logowania —
        // niezgodność = uwierzytelnienie odrzucone przy następnym żądaniu.
        public int AuthVersion { get; set; } = 1;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public Subscription? Subscription { get; set; }
    }

    public enum AccountStatus
    {
        // Normalne konto.
        Active,

        // Może czytać i się logować; każdy zapis (post, komentarz, reakcja, zgłoszenie) jest odrzucany.
        Suspended,

        // Nie może się zalogować ani odświeżyć tokena; istniejące sesje są odrzucane. Dane nie są usuwane.
        Banned
    }

    // Jedyny wiersz (Id = 1) — blokowany FOR UPDATE, serializuje rejestracje we wszystkich instancjach aplikacji.
    public class RegistrationGate
    {
        public const int SingletonId = 1;

        public int Id { get; set; }
    }
}
