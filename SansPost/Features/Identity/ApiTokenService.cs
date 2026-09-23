using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost.Features.Identity
{
    // Tokeny REST API: krótkotrwały JWT + rotowany refresh token (w bazie tylko hash).
    public interface IApiTokenService
    {
        Task<TokenResponse> IssueAsync(AuthenticatedUser user, CancellationToken cancellationToken = default);

        // null = token nieznany, wygasły, unieważniony lub użyty ponownie.
        Task<TokenResponse?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default);

        Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken = default);
    }

    public class ApiTokenService : IApiTokenService
    {
        private readonly ApplicationDbContext _context;
        private readonly JwtTokenService _jwt;
        private readonly JwtOptions _options;
        private readonly TimeProvider _time;

        public ApiTokenService(ApplicationDbContext context, JwtTokenService jwt, IOptions<JwtOptions> options, TimeProvider time)
        {
            _context = context;
            _jwt = jwt;
            _options = options.Value;
            _time = time;
        }

        public async Task<TokenResponse> IssueAsync(AuthenticatedUser user, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var (refreshToken, entity) = CreateRefreshToken(user.Id, now);

            _context.RefreshTokens.Add(entity);
            await _context.SaveChangesAsync(cancellationToken);

            return CreateResponse(user, refreshToken, entity);
        }

        public async Task<TokenResponse?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                return null;

            var now = _time.GetUtcNow().UtcDateTime;
            var hash = HashToken(refreshToken);

            var stored = await _context.RefreshTokens
                .AsNoTracking()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

            if (stored is null)
                return null;

            if (stored.RevokedAt is not null)
            {
                // Ponowne użycie zrotowanego tokena = możliwa kradzież. Unieważnij wszystkie sesje API użytkownika.
                if (stored.ReplacedByTokenId is not null)
                    await RevokeAllForUserAsync(stored.UserId, now, cancellationToken);

                return null;
            }

            if (stored.ExpiresAt <= now || !PasswordHasher.IsSupportedHash(stored.User.PasswordHash))
                return null;

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // Atomowe "zajęcie" tokena — równoległe użycie tego samego tokena przegra ten wyścig.
            var claimed = await _context.RefreshTokens
                .Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
            if (claimed == 0)
                return null;

            var (newRefreshToken, entity) = CreateRefreshToken(stored.UserId, now);
            _context.RefreshTokens.Add(entity);
            await _context.SaveChangesAsync(cancellationToken);

            await _context.RefreshTokens
                .Where(t => t.Id == stored.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedByTokenId, entity.Id), cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // Claims z aktualnego stanu użytkownika (np. zmieniona rola).
            return CreateResponse(AuthenticatedUser.From(stored.User), newRefreshToken, entity);
        }

        public async Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                return;

            var hash = HashToken(refreshToken);
            var now = _time.GetUtcNow().UtcDateTime;

            await _context.RefreshTokens
                .Where(t => t.TokenHash == hash && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }

        public static string HashToken(string refreshToken) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

        private Task RevokeAllForUserAsync(int userId, DateTime now, CancellationToken cancellationToken) =>
            _context.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        private (string RawToken, RefreshToken Entity) CreateRefreshToken(int userId, DateTime now)
        {
            var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var entity = new RefreshToken
            {
                UserId = userId,
                TokenHash = HashToken(rawToken),
                CreatedAt = now,
                ExpiresAt = now.Add(_options.RefreshTokenLifetime)
            };

            return (rawToken, entity);
        }

        private TokenResponse CreateResponse(AuthenticatedUser user, string refreshToken, RefreshToken entity)
        {
            var identity = UserClaimsFactory.CreateIdentity(user, JwtBearerDefaults.AuthenticationScheme);
            var (accessToken, accessExpiresAt) = _jwt.CreateAccessToken(identity);

            return new TokenResponse(accessToken, accessExpiresAt, refreshToken, entity.ExpiresAt);
        }
    }
}
