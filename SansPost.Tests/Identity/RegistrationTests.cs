using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Identity
{
    public class RegistrationTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        private async Task<ServiceResult<UserResponse>> RegisterAsync(string username, string email, string password = TestUsers.Password)
        {
            using var context = _db.CreateContext();
            return await _db.CreateAuthService(context).RegisterAsync(new RegisterRequest
            {
                Username = username,
                Email = email,
                Password = password
            });
        }

        [Fact]
        public async Task Register_StoresBcryptHash_NotPlaintext()
        {
            var result = await RegisterAsync("anna", "anna@example.com");

            Assert.True(result.Succeeded);
            using var context = _db.CreateContext();
            var user = await context.Users.SingleAsync();
            Assert.NotEqual(TestUsers.Password, user.PasswordHash);
            Assert.True(PasswordHasher.IsSupportedHash(user.PasswordHash));
            Assert.True(BCrypt.Net.BCrypt.Verify(TestUsers.Password, user.PasswordHash));
        }

        [Fact]
        public async Task Register_AlwaysCreatesUserRoleWithFreeSubscription()
        {
            var result = await RegisterAsync("bartek", "bartek@example.com");

            Assert.Equal(UserRole.User, result.Value!.Role);
            Assert.Equal(SubscriptionType.Free, result.Value.Subscription);

            using var context = _db.CreateContext();
            var user = await context.Users.Include(u => u.Subscription).SingleAsync();
            Assert.Equal(UserRole.User, user.Role);
            Assert.Equal(SubscriptionType.Free, user.Subscription!.Type);
        }

        [Fact]
        public async Task Register_NormalizesIdentityFields()
        {
            await RegisterAsync("Celina", "  Celina@Example.COM ");

            using var context = _db.CreateContext();
            var user = await context.Users.SingleAsync();
            Assert.Equal("Celina", user.Username);
            Assert.Equal("CELINA", user.NormalizedUsername);
            Assert.Equal("Celina@Example.COM", user.Email);
            Assert.Equal("CELINA@EXAMPLE.COM", user.NormalizedEmail);
        }

        [Fact]
        public async Task Register_RejectsDuplicateEmail_CaseInsensitive()
        {
            await RegisterAsync("first", "Test@Example.com");

            var duplicate = await RegisterAsync("second", "test@example.com");

            Assert.Equal(ServiceError.Conflict, duplicate.Error);
        }

        [Fact]
        public async Task Register_RejectsDuplicateUsername_CaseInsensitive()
        {
            await RegisterAsync("Dawid", "dawid1@example.com");

            var duplicate = await RegisterAsync("dAWID", "dawid2@example.com");

            Assert.Equal(ServiceError.Conflict, duplicate.Error);
        }

        [Theory]
        [InlineData("short")]                                                              // < 8 znaków
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // 65 znaków
        [InlineData("żżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżż")]                            // 40 znaków, 80 bajtów > limit BCrypt
        [InlineData("        ")]
        public async Task Register_RejectsPasswordOutsidePolicy(string password)
        {
            var result = await RegisterAsync("eliza", "eliza@example.com", password);

            Assert.Equal(ServiceError.Validation, result.Error);
        }

        [Fact]
        public async Task Database_EnforcesUniqueNormalizedEmail_EvenWithoutServiceCheck()
        {
            await RegisterAsync("filip", "filip@example.com");

            using var context = _db.CreateContext();
            context.Users.Add(new User
            {
                Username = "other",
                NormalizedUsername = "OTHER",
                Email = "FILIP@example.com",
                NormalizedEmail = "FILIP@EXAMPLE.COM",
                PasswordHash = PasswordHasher.Hash(TestUsers.Password)
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        public void Dispose() => _db.Dispose();
    }
}
