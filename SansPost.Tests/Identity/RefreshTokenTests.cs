using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Identity
{
    public class RefreshTokenTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        private async Task<TokenResponse> IssueAsync(AuthenticatedUser user)
        {
            using var context = _db.CreateContext();
            return await _db.CreateTokenService(context).IssueAsync(user);
        }

        private async Task<TokenResponse?> RefreshAsync(string refreshToken)
        {
            using var context = _db.CreateContext();
            return await _db.CreateTokenService(context).RefreshAsync(refreshToken);
        }

        [Fact]
        public async Task ValidRefreshToken_IssuesNewPair()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("jan"));

            var refreshed = await RefreshAsync(tokens.RefreshToken);

            Assert.NotNull(refreshed);
            Assert.False(string.IsNullOrEmpty(refreshed!.AccessToken));
            Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        }

        [Fact]
        public async Task Refresh_RotatesToken_RevokingAndLinkingThePreviousOne()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("kasia"));
            var refreshed = await RefreshAsync(tokens.RefreshToken);

            using var context = _db.CreateContext();
            var old = await context.RefreshTokens.SingleAsync(t => t.TokenHash == ApiTokenService.HashToken(tokens.RefreshToken));
            var current = await context.RefreshTokens.SingleAsync(t => t.TokenHash == ApiTokenService.HashToken(refreshed!.RefreshToken));

            Assert.NotNull(old.RevokedAt);
            Assert.Equal(current.Id, old.ReplacedByTokenId);
            Assert.Null(current.RevokedAt);
        }

        [Fact]
        public async Task ReusedRotatedToken_IsRejected_AndRevokesTheWholeSession()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("leon"));
            var refreshed = await RefreshAsync(tokens.RefreshToken);

            Assert.Null(await RefreshAsync(tokens.RefreshToken));

            // Wykryte ponowne użycie: token wydany w rotacji też zostaje unieważniony.
            Assert.Null(await RefreshAsync(refreshed!.RefreshToken));
        }

        [Fact]
        public async Task ExpiredRefreshToken_IsRejected()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("marta"));

            _db.Time.Advance(TestDatabase.JwtOptions.RefreshTokenLifetime + TimeSpan.FromMinutes(1));

            Assert.Null(await RefreshAsync(tokens.RefreshToken));
        }

        [Fact]
        public async Task RevokedRefreshToken_IsRejected()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("nina"));

            using (var context = _db.CreateContext())
                await _db.CreateTokenService(context).RevokeAsync(tokens.RefreshToken);

            Assert.Null(await RefreshAsync(tokens.RefreshToken));
        }

        [Fact]
        public async Task UnknownRefreshToken_IsRejected()
        {
            Assert.Null(await RefreshAsync("not-a-real-token"));
        }

        [Fact]
        public async Task RefreshToken_IsStoredOnlyAsSha256Hash()
        {
            var tokens = await IssueAsync(await _db.RegisterAsync("olek"));

            using var context = _db.CreateContext();
            var stored = await context.RefreshTokens.SingleAsync();

            Assert.NotEqual(tokens.RefreshToken, stored.TokenHash);
            Assert.Equal(64, stored.TokenHash.Length);
            Assert.Equal(ApiTokenService.HashToken(tokens.RefreshToken), stored.TokenHash);
            Assert.False(await context.RefreshTokens.AnyAsync(t => t.TokenHash == tokens.RefreshToken));
        }

        public void Dispose() => _db.Dispose();
    }
}
