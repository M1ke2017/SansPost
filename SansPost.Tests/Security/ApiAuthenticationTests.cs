using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    public class ApiAuthenticationTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public ApiAuthenticationTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Register_IgnoresClientSuppliedRoleSubscriptionIdAndHash()
        {
            var username = TestUsers.UniqueName();
            var client = _factory.CreateHttpsClient();

            var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username,
                email = $"{username}@example.com",
                password = TestUsers.Password,
                role = "Admin",
                subscription = "Premium",
                userId = 999_999,
                passwordHash = "plaintext"
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);

            var created = JsonSerializer.Deserialize<UserResponse>(body, SansPostFactory.Json)!;
            Assert.Equal(UserRole.User, created.Role);
            Assert.Equal(SubscriptionType.Free, created.Subscription);
            Assert.NotEqual(999_999, created.Id);

            var stored = _factory.WithScope(db => db.Users.Single(u => u.Id == created.Id));
            Assert.Equal(UserRole.User, stored.Role);
            Assert.True(PasswordHasher.IsSupportedHash(stored.PasswordHash));
        }

        [Fact]
        public async Task Login_WrongPasswordAndUnknownUser_ReturnIdenticalGeneric401()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);
            var client = _factory.CreateHttpsClient();

            var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { email = $"{username}@example.com", password = "wrong-password-1" });
            var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new { email = "ghost@example.com", password = "wrong-password-1" });

            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

            var first = await wrongPassword.Content.ReadFromJsonAsync<ProblemDetails>();
            var second = await unknownUser.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal(first!.Detail, second!.Detail);
            Assert.Equal(first.Title, second.Title);
            Assert.DoesNotContain("ghost", second.Detail);
        }

        [Fact]
        public async Task Login_ReturnsShortLivedAccessTokenWithUnifiedClaims()
        {
            var username = TestUsers.UniqueName();
            var userId = await _factory.CreateUserAsync(username);

            var tokens = await _factory.LoginApiAsync(_factory.CreateHttpsClient(), username);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);

            var lifetime = jwt.ValidTo - jwt.ValidFrom;
            Assert.InRange(lifetime, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20));
            Assert.Equal(userId.ToString(), jwt.Claims.Single(c => c.Type == "nameid").Value);
            Assert.Equal(username, jwt.Claims.Single(c => c.Type == "unique_name").Value);
            Assert.Equal($"{username}@example.com", jwt.Claims.Single(c => c.Type == "email").Value);
            Assert.Equal("User", jwt.Claims.Single(c => c.Type == "role").Value);
            Assert.DoesNotContain(jwt.Claims, c => c.Value.Contains("$2", StringComparison.Ordinal));
        }

        public static TheoryData<string, HttpStatusCode> TokenVariants => new()
        {
            { "valid", HttpStatusCode.OK },
            { "expired", HttpStatusCode.Unauthorized },
            { "wrong-signature", HttpStatusCode.Unauthorized },
            { "wrong-issuer", HttpStatusCode.Unauthorized },
            { "wrong-audience", HttpStatusCode.Unauthorized },
            { "unsigned", HttpStatusCode.Unauthorized }
        };

        [Theory]
        [MemberData(nameof(TokenVariants))]
        public async Task JwtValidation_EnforcesSignatureIssuerAudienceAndLifetime(string variant, HttpStatusCode expected)
        {
            var username = TestUsers.UniqueName();
            var userId = await _factory.CreateUserAsync(username);
            var now = DateTime.UtcNow;

            var token = variant switch
            {
                "valid" => CreateToken(userId),
                "expired" => CreateToken(userId, notBefore: now.AddMinutes(-30), expires: now.AddMinutes(-5)),
                "wrong-signature" => CreateToken(userId, key: "attacker-signing-key-0123456789abcdef!!"),
                "wrong-issuer" => CreateToken(userId, issuer: "evil-issuer"),
                "wrong-audience" => CreateToken(userId, audience: "someone-else"),
                "unsigned" => CreateUnsignedToken(userId),
                _ => throw new ArgumentOutOfRangeException(nameof(variant))
            };

            var client = _factory.CreateHttpsClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/api/auth/me");

            Assert.Equal(expected, response.StatusCode);
        }

        [Fact]
        public async Task RefreshEndpoint_RotatesAndRejectsReuse()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);
            var client = _factory.CreateHttpsClient();
            var tokens = await _factory.LoginApiAsync(client, username);

            var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
            var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        }

        [Fact]
        public async Task Logout_RevokesRefreshToken()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);
            var client = _factory.CreateHttpsClient();
            var tokens = await _factory.LoginApiAsync(client, username);

            var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken });
            var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        }

        private static string CreateToken(
            int userId,
            string key = SansPostFactory.JwtKey,
            string issuer = SansPostFactory.Issuer,
            string audience = SansPostFactory.Audience,
            DateTime? notBefore = null,
            DateTime? expires = null)
        {
            var now = DateTime.UtcNow;
            var handler = new JwtSecurityTokenHandler();

            return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Role, nameof(UserRole.User)),
                    new Claim(SansPostClaimTypes.AuthVersion, "1")
                }),
                Issuer = issuer,
                Audience = audience,
                IssuedAt = notBefore ?? now,
                NotBefore = notBefore ?? now,
                Expires = expires ?? now.AddMinutes(10),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
            }));
        }

        private static string CreateUnsignedToken(int userId)
        {
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                SansPostFactory.Issuer,
                SansPostFactory.Audience,
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
                now,
                now.AddMinutes(10));

            return new JwtSecurityTokenHandler().WriteToken(token); // alg: none
        }
    }
}
