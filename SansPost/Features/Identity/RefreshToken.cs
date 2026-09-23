namespace SansPost.Features.Identity
{
    // Refresh token REST API. W bazie wyłącznie SHA-256 surowej wartości.
    public class RefreshToken
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; } = null!;

        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }

        // Ustawiane przy rotacji; użycie tokena z tą wartością oznacza reuse.
        public int? ReplacedByTokenId { get; set; }
    }
}
