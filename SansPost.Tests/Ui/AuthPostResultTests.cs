using SansPost.Shared;

namespace SansPost.Tests.Ui
{
    // Sprint 14D — karty Saloonu czytają wynik natywnego POST /auth/* z adresu końcowego po przekierowaniach
    // AccountController. Sukces tylko przy zalogowaniu i lądowaniu w /saloon; reszta to kod błędu dla karty.
    [Trait("Category", "Ui")]
    public class AuthPostResultTests
    {
        [Fact]
        public void SignedIn_OnlyWhenLoginEndsInSaloon()
        {
            Assert.True(new AuthPostResult("login", 200, "/saloon", "").SignedIn);
            Assert.Null(new AuthPostResult("login", 200, "/saloon", "").Error);
            Assert.False(new AuthPostResult("register", 200, "/saloon", "").SignedIn);
            Assert.False(new AuthPostResult("login", 200, "/login", "?error=invalid&returnUrl=%2Fsaloon").SignedIn);
            Assert.False(new AuthPostResult("login", 500, "/saloon", "").SignedIn);
        }

        [Theory]
        [InlineData("login", 200, "/login", "?error=invalid&returnUrl=%2Fsaloon", "invalid")]
        [InlineData("login", 429, "/auth/login", "", "rate-limited")]
        [InlineData("login", 0, "", "", "network")]
        [InlineData("register", 200, "/register", "?error=mismatch", "mismatch")]
        [InlineData("register", 200, "/register", "?error=alias-taken", "alias-taken")]
        [InlineData("register", 200, "/register", "?error=capacity", "capacity")]
        [InlineData("register", 400, "/auth/register", "", "invalid")]
        public void Error_MapsRedirectOrStatus(string stage, int status, string path, string query, string expected) =>
            Assert.Equal(expected, new AuthPostResult(stage, status, path, query).Error);
    }
}
