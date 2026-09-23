using System.Text.RegularExpressions;

namespace SansPost.Features.Identity
{
    // Jedyna aktywna ścieżka hashowania haseł: BCrypt.
    public static class PasswordHasher
    {
        private const int WorkFactor = 12;

        // Format BCrypt: $2a$/$2b$/$2x$/$2y$, koszt, 53 znaki soli+hasha.
        private static readonly Regex BcryptFormat = new(@"^\$2[abxy]\$\d{2}\$[./A-Za-z0-9]{53}$", RegexOptions.Compiled);

        // Hash używany, gdy konto nie istnieje lub nie ma poprawnego hasha — wyrównuje czas odpowiedzi.
        private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), WorkFactor));

        public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

        public static bool IsSupportedHash(string? passwordHash) =>
            passwordHash is not null && BcryptFormat.IsMatch(passwordHash);

        // Nigdy nie porównuje z wartością nie-BCrypt (np. legacy plaintext) — takie konto jest zawsze odrzucane.
        public static bool Verify(string password, string? passwordHash)
        {
            if (!IsSupportedHash(passwordHash))
            {
                SimulateVerification(password);
                return false;
            }

            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }

        public static void SimulateVerification(string password) => BCrypt.Net.BCrypt.Verify(password, DummyHash.Value);
    }
}
