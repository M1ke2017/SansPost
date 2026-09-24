using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Identity
{
    public class RegistrationTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        private async Task<ServiceResult<UserResponse>> RegisterAsync(string? username, string email, string password = TestUsers.Password)
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
            var result = await RegisterAsync("DustyFox", "anna@example.com");

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
            var result = await RegisterAsync("SilverHawk", "bartek@example.com");

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
            await RegisterAsync("CopperWren", "  Celina@Example.COM ");

            using var context = _db.CreateContext();
            var user = await context.Users.SingleAsync();
            Assert.Equal("CopperWren", user.Username);
            Assert.Equal("COPPERWREN", user.NormalizedUsername);
            Assert.Equal("Celina@Example.COM", user.Email);
            Assert.Equal("CELINA@EXAMPLE.COM", user.NormalizedEmail);
        }

        [Fact]
        public async Task Register_RejectsDuplicateEmail_CaseInsensitive()
        {
            await RegisterAsync("MesaOwl", "Test@Example.com");

            var duplicate = await RegisterAsync("SageLark", "test@example.com");

            Assert.Equal(ServiceError.Conflict, duplicate.Error);
            Assert.Equal(AuthService.IdentityTakenCode, duplicate.Code);
        }

        // Przydomek wyświetlony w formularzu zajęty w międzyczasie → 409 alias-taken (nie 500, nie ogólny konflikt).
        [Fact]
        public async Task Register_TakenAlias_ReturnsAliasTakenConflict()
        {
            await RegisterAsync("LoneWolf", "dawid1@example.com");

            var duplicate = await RegisterAsync("LoneWolf", "dawid2@example.com");

            Assert.Equal(ServiceError.Conflict, duplicate.Error);
            Assert.Equal(AuthService.AliasTakenCode, duplicate.Code);
        }

        // G2/G6 — dowolna nazwa, także podmieniona w requeście (ukryte pole / REST) lub inna wielkość liter, jest odrzucana.
        [Theory]
        [InlineData("Dawid")]
        [InlineData("dustyraven")]
        [InlineData("Dusty Raven")]
        [InlineData("DustyRaven1")]
        [InlineData("DustyAdmin")]
        [InlineData("MesaMesa")]
        public async Task Register_RejectsNonCuratedUsername(string username)
        {
            var result = await RegisterAsync(username, "eve@example.com");

            Assert.Equal(ServiceError.Validation, result.Error);
            using var context = _db.CreateContext();
            Assert.Empty(context.Users);
        }

        [Fact]
        public async Task Register_WellFormedButNonCuratedName_HasAliasNotCuratedCode()
        {
            var result = await RegisterAsync("CowboyBob", "bob@example.com");

            Assert.Equal(AuthService.AliasNotCuratedCode, result.Code);
        }

        // Rejestracja bez przydomka: serwer przydziela wolny przydomek z generatora.
        [Fact]
        public async Task Register_WithoutUsername_AssignsCuratedAlias()
        {
            var result = await RegisterAsync(null, "auto@example.com");

            Assert.True(result.Succeeded, result.Message);
            Assert.True(WesternAliases.IsCurated(result.Value!.Username));
        }

        [Fact]
        public async Task Register_RejectsReservedDemoEmailDomain()
        {
            var result = await RegisterAsync("RedFox", "someone@demo.sanspost.invalid");

            Assert.Equal(ServiceError.Validation, result.Error);
        }

        [Theory]
        [InlineData("short")]                                                              // < 8 znaków
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // 65 znaków
        [InlineData("żżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżżż")]                            // 40 znaków, 80 bajtów > limit BCrypt
        [InlineData("        ")]
        public async Task Register_RejectsPasswordOutsidePolicy(string password)
        {
            var result = await RegisterAsync("WildRose", "eliza@example.com", password);

            Assert.Equal(ServiceError.Validation, result.Error);
        }

        [Fact]
        public async Task Database_EnforcesUniqueNormalizedEmail_EvenWithoutServiceCheck()
        {
            await RegisterAsync("RockyBison", "filip@example.com");

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

        // G5 — UNIQUE na znormalizowanej nazwie chroni przydomek także z pominięciem serwisu.
        [Fact]
        public async Task Database_EnforcesUniqueAlias_EvenWithoutServiceCheck()
        {
            await RegisterAsync("CedarHawk", "gabi@example.com");

            using var context = _db.CreateContext();
            context.Users.Add(new User
            {
                Username = "CedarHawk",
                NormalizedUsername = "CEDARHAWK",
                Email = "other@example.com",
                NormalizedEmail = "OTHER@EXAMPLE.COM",
                PasswordHash = PasswordHasher.Hash(TestUsers.Password)
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        public void Dispose() => _db.Dispose();
    }

    [Trait("Category", "Aliases")]
    public class WesternAliasTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        // G1/G3 — każdy przydomek: prefiks + sufiks ze słownika, PascalCase, spełnia istniejące reguły nazwy konta.
        [Fact]
        public void AllAliases_AreCuratedPascalCaseAndMatchUsernameConstraints()
        {
            Assert.True(WesternAliases.All.Count > 1000);
            Assert.Equal(WesternAliases.All.Count, WesternAliases.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var alias in WesternAliases.All)
            {
                Assert.Matches("^([A-Z][a-z]+){2}$", alias);
                Assert.Contains(WesternAliases.Prefixes, p => alias.StartsWith(p, StringComparison.Ordinal)
                    && WesternAliases.Suffixes.Contains(alias[p.Length..]) && alias[p.Length..] != p);
                Assert.Null(RequestValidator.Validate(new RegisterRequest { Username = alias, Email = "a@example.com", Password = TestUsers.Password }));
            }
        }

        [Fact]
        public async Task Generator_ReturnsCuratedFreeAlias()
        {
            using var context = _db.CreateContext();

            for (var i = 0; i < 20; i++)
                Assert.True(WesternAliases.IsCurated(await new AliasGenerator(context).SuggestAsync()));
        }

        // G4 — zajęte przydomki są pomijane: przy jednym wolnym generator zawsze zwraca właśnie jego.
        [Fact]
        public async Task Generator_SkipsTakenAliases()
        {
            var free = WesternAliases.All[^1];
            using (var context = _db.CreateContext())
            {
                context.Users.AddRange(WesternAliases.All.Where(a => a != free).Select((alias, i) => new User
                {
                    Username = alias,
                    NormalizedUsername = IdentityNormalizer.Normalize(alias),
                    Email = $"u{i}@example.com",
                    NormalizedEmail = $"U{i}@EXAMPLE.COM",
                    PasswordHash = "test-data-no-login"
                }));
                await context.SaveChangesAsync();
            }

            using var fresh = _db.CreateContext();
            Assert.Equal(free, await new AliasGenerator(fresh).SuggestAsync());

            fresh.Users.Add(new User { Username = free, NormalizedUsername = IdentityNormalizer.Normalize(free), Email = "last@example.com", NormalizedEmail = "LAST@EXAMPLE.COM", PasswordHash = "x" });
            await fresh.SaveChangesAsync();
            Assert.Null(await new AliasGenerator(fresh).SuggestAsync());
        }

        // G7 — propozycja (reroll) niczego nie tworzy ani nie rezerwuje.
        [Fact]
        public async Task Suggestion_DoesNotCreateAccounts()
        {
            using var context = _db.CreateContext();
            var generator = new AliasGenerator(context);
            for (var i = 0; i < 5; i++)
                await generator.SuggestAsync();

            Assert.Empty(context.Users);
        }

        public void Dispose() => _db.Dispose();
    }
}
