using SansPost.Shared.Bar;

namespace SansPost.Tests.Ui
{
    // Sprint 17 — stan tablicy Wanted w adresie /saloon (ten sam model co BAR): odczyt, adres, poziom wyżej, strefa sali.
    public class WantedStateTests
    {
        private static BarState? Parse(string? wanted, string? id = null, string? next = null, string? back = null, string? bar = null) =>
            BarState.Parse(bar, null, null, null, id, null, next, back, wanted);

        [Fact]
        public void Board_RoundTrips_AndClosesToHall()
        {
            var state = Parse("board");

            Assert.Equal(BarState.Wanted, state);
            Assert.Equal("/saloon?wanted=board", state!.Url);
            Assert.Null(state.Parent);                     // Escape / "wstecz" z tablicy = sala
            Assert.True(state.IsWanted);
            Assert.Equal("wanted", state.Zone);
            Assert.Equal("Tablica Wanted", state.Title);
        }

        [Fact]
        public void ConversationFromBoard_RoundTrips_AndGoesBackToBoard_NotToBar()
        {
            var state = Parse("post", id: "42");

            Assert.Equal(BarState.Conversation(42, BarState.Wanted), state);
            Assert.Equal("/saloon?wanted=post&id=42", state!.Url);
            Assert.Equal(BarState.Wanted, state.Parent);
            Assert.Equal("wanted", state.Zone);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("abc")]
        public void ConversationWithoutValidId_FallsBackToBoard(string? id) =>
            Assert.Equal(BarState.Wanted, Parse("post", id: id));

        [Fact]
        public void UnknownWantedValue_IsHall_AndBarParameterWins()
        {
            Assert.Null(Parse("season"));
            Assert.Equal(BarState.Menu, Parse("board", bar: "menu"));
        }

        [Fact]
        public void LoginOverConversationFromBoard_RoundTrips_WithBackToThatConversation()
        {
            var conversation = BarState.Conversation(7, BarState.Wanted);
            var login = BarState.Auth(BarView.Login, conversation.Url, conversation);

            Assert.StartsWith("/saloon?wanted=login&", login.Url);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Url[(login.Url.IndexOf('?') + 1)..]);
            var parsed = Parse(query["wanted"], next: query["next"], back: query["back"]);

            Assert.Equal(login, parsed);
            Assert.Equal(conversation, parsed!.Parent);
            Assert.Equal("wanted", parsed.Zone);
        }

        [Theory]
        [InlineData("https://evil.example/")]
        [InlineData("//evil.example/")]
        [InlineData("/\\evil.example/")]
        public void LoginNext_AcceptsOnlyLocalPaths(string next)
        {
            var state = Parse("login", next: next);

            Assert.Equal(BarView.Login, state!.View);
            Assert.Equal("/saloon?wanted=board", state.Next);   // zewnętrzny adres odrzucony — powrót do tablicy
        }

        [Fact]
        public void BarStates_StayInBarZone()
        {
            Assert.Equal("bar", BarState.Menu.Zone);
            Assert.Equal("bar", BarState.Conversation(3, new BarState(BarView.Newest)).Zone);
            Assert.StartsWith("/saloon?bar=post&id=3", BarState.Conversation(3, new BarState(BarView.Newest)).Url);
        }
    }
}
