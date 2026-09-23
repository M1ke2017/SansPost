using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Identity
{
    public class LoginTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        private async Task<AuthenticatedUser?> AuthenticateAsync(string email, string password)
        {
            using var context = _db.CreateContext();
            return await _db.CreateAuthService(context).AuthenticateAsync(email, password);
        }

        [Fact]
        public async Task ValidCredentials_ReturnUserWithoutSecrets()
        {
            await _db.RegisterAsync("gosia");

            var user = await AuthenticateAsync("gosia@example.com", TestUsers.Password);

            Assert.NotNull(user);
            Assert.Equal("gosia", user!.Username);
            Assert.Equal(UserRole.User, user.Role);
        }

        [Fact]
        public async Task EmailLookup_IsCaseAndWhitespaceInsensitive()
        {
            await _db.RegisterAsync("henryk");

            Assert.NotNull(await AuthenticateAsync("  HENRYK@Example.com ", TestUsers.Password));
        }

        [Fact]
        public async Task WrongPassword_IsRejected()
        {
            await _db.RegisterAsync("iga");

            Assert.Null(await AuthenticateAsync("iga@example.com", "wrong-password-123"));
        }

        [Fact]
        public async Task UnknownUser_IsRejectedTheSameWay()
        {
            Assert.Null(await AuthenticateAsync("nobody@example.com", TestUsers.Password));
        }

        [Fact]
        public async Task LegacyPlaintextPassword_IsNeverAccepted()
        {
            // Konto z czasów starego UsersController.Register: hasło zapisane jawnie w PasswordHash.
            using (var context = _db.CreateContext())
            {
                context.Users.Add(new User
                {
                    Username = "legacy",
                    NormalizedUsername = "LEGACY",
                    Email = "legacy@example.com",
                    NormalizedEmail = "LEGACY@EXAMPLE.COM",
                    PasswordHash = "PlaintextSecret1"
                });
                await context.SaveChangesAsync();
            }

            Assert.Null(await AuthenticateAsync("legacy@example.com", "PlaintextSecret1"));
        }

        public void Dispose() => _db.Dispose();
    }
}
