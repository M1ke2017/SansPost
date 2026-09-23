using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SansPost.Infrastructure.Security
{
    // Wystawianie access tokenów i parametry ich walidacji (używane przez JwtBearer).
    public sealed class JwtTokenService
    {
        private readonly JwtOptions _options;
        private readonly TimeProvider _time;
        private readonly SigningCredentials _signingCredentials;
        private readonly JwtSecurityTokenHandler _handler = new();

        public JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
        {
            _options = options.Value;
            _time = time;

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
            _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            ValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            };
        }

        public TokenValidationParameters ValidationParameters { get; }

        public (string Token, DateTime ExpiresAt) CreateAccessToken(ClaimsIdentity subject)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var expiresAt = now.Add(_options.AccessTokenLifetime);

            var descriptor = new SecurityTokenDescriptor
            {
                Subject = subject,
                Issuer = _options.Issuer,
                Audience = _options.Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = expiresAt,
                SigningCredentials = _signingCredentials
            };

            return (_handler.WriteToken(_handler.CreateToken(descriptor)), expiresAt);
        }
    }
}
