using System.Text;

namespace SansPost.Features.Identity
{
    // Polityka oparta na długości (zgodnie z NIST SP 800-63B), bez wymuszania klas znaków.
    public static class PasswordPolicy
    {
        public const int MinLength = 8;
        public const int MaxLength = 64;

        // BCrypt uwzględnia tylko pierwsze 72 bajty — dłuższe hasło byłoby po cichu obcięte.
        private const int MaxBcryptBytes = 72;

        public static string? Validate(string? password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return "Hasło jest wymagane.";

            if (password.Length < MinLength || password.Length > MaxLength)
                return $"Hasło musi mieć od {MinLength} do {MaxLength} znaków.";

            if (Encoding.UTF8.GetByteCount(password) > MaxBcryptBytes)
                return "Hasło jest zbyt długie.";

            return null;
        }
    }
}
