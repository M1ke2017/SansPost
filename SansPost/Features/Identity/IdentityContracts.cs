using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Identity
{
    // Jedyne pola, które klient może podać przy rejestracji. Role/Subscription/UserId/PasswordHash ustala serwer.
    public sealed class RegisterRequest
    {
        [Required(ErrorMessage = "Nazwa użytkownika jest wymagana.")]
        [RegularExpression(@"^[\p{L}\p{N}._-]{3,50}$",
            ErrorMessage = "Nazwa użytkownika: 3–50 znaków, litery, cyfry oraz . _ -")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email jest wymagany.")]
        [EmailAddress(ErrorMessage = "Nieprawidłowy adres email.")]
        [StringLength(254, ErrorMessage = "Email jest zbyt długi.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hasło jest wymagane.")]
        [StringLength(PasswordPolicy.MaxLength, MinimumLength = PasswordPolicy.MinLength,
            ErrorMessage = "Hasło musi mieć od 8 do 64 znaków.")]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginRequest
    {
        [Required]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class RefreshTokenRequest
    {
        [Required]
        public string RefreshToken { get; set; } = string.Empty;
    }

    public sealed record TokenResponse(
        string AccessToken,
        DateTime AccessTokenExpiresAt,
        string RefreshToken,
        DateTime RefreshTokenExpiresAt)
    {
        public string TokenType => "Bearer";
    }

    // Publiczny widok użytkownika. Celowo bez Email i PasswordHash.
    public sealed record UserResponse(
        int Id,
        string Username,
        UserRole Role,
        SubscriptionType Subscription,
        DateTime CreatedAt)
    {
        public static UserResponse From(User user, DateTime utcNow) => new(
            user.Id,
            user.Username,
            user.Role,
            Identity.Subscription.EffectiveTypeOf(user.Subscription, utcNow),
            user.CreatedAt);
    }

    public sealed class ChangeRoleRequest
    {
        [Required]
        public UserRole? Role { get; set; }
    }

    public sealed class SetSubscriptionRequest
    {
        [Required]
        public SubscriptionType? Type { get; set; }

        public DateTime? ExpiresAt { get; set; }
    }
}
