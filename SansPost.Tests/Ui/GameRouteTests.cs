using SansPost.Shared.Bar;

namespace SansPost.Tests.Ui
{
    // Sprint 19 — Stół gry w adresie /saloon (ten sam model co BAR, Wanted i Music) i karta logowania gościa nad stołem.
    [Trait("Category", "Duels")]
    public class GameRouteTests
    {
        private static BarState? Parse(string? game, string? next = null, string? back = null) =>
            BarState.Parse(null, null, null, null, next: next, back: back, game: game);

        [Fact]
        public void GameTable_RoundTrips_ClosesToHall_InGameZone()
        {
            var state = Parse("table");

            Assert.Equal(BarState.Game, state);
            Assert.Equal("/saloon?game=table", state!.Url);
            Assert.Null(state.Parent);
            Assert.Equal("game", state.Zone);
            Assert.True(state.IsGame);
            Assert.Equal("Śladem Rewolwerowca", state.Title);
        }

        [Fact]
        public void LoginOverTable_RoundTrips_BackToTable()
        {
            var login = BarState.Auth(BarView.Login, BarState.Game.Url, BarState.Game);

            Assert.StartsWith("/saloon?game=login&", login.Url);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Url[(login.Url.IndexOf('?') + 1)..]);
            var parsed = Parse(query["game"], next: query["next"], back: query["back"]);

            Assert.Equal(login, parsed);
            Assert.Equal(BarState.Game, parsed!.Parent);
            Assert.Equal("game", parsed.Zone);
            Assert.Equal("/saloon?game=table", parsed.Next);
        }

        [Theory]
        [InlineData("https://evil.example/")]
        [InlineData("//evil.example/")]
        public void LoginNext_AcceptsOnlyLocalPaths(string next) =>
            Assert.Equal("/saloon?game=table", Parse("login", next: next)!.Next);

        [Fact]
        public void UnknownGameValue_IsHall_OtherZonesTakePrecedence()
        {
            Assert.Null(Parse("poker"));
            Assert.Equal(BarState.Menu, BarState.Parse("menu", null, null, null, game: "table"));
            Assert.Equal(BarState.Music, BarState.Parse(null, null, null, null, music: "radio", game: "table"));
        }
    }
}
