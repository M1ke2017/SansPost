using SansPost.Shared.Bar;
using SansPost.Shared.Music;

namespace SansPost.Tests.Ui
{
    // Sprint 18 — Kącik muzyczny w adresie /saloon (ten sam model co BAR i Wanted) i teksty stanu radia dla czytnika.
    [Trait("Category", "Music")]
    public class MusicRouteTests
    {
        [Fact]
        public void MusicRadio_RoundTrips_ClosesToHall_InMusicZone()
        {
            var state = BarState.Parse(null, null, null, null, music: "radio");

            Assert.Equal(BarState.Music, state);
            Assert.Equal("/saloon?music=radio", state!.Url);
            Assert.Null(state.Parent);                  // Escape / "wstecz" = sala
            Assert.Equal("music", state.Zone);
            Assert.False(state.IsWanted);
            Assert.Equal("Muzyka w Saloonie", state.Title);
        }

        [Theory]
        [InlineData("playlist")]
        [InlineData("")]
        public void UnknownMusicValue_IsHall(string value) =>
            Assert.Null(BarState.Parse(null, null, null, null, music: value));

        [Fact]
        public void BarAndWantedParameters_TakePrecedenceOverMusic()
        {
            Assert.Equal(BarState.Menu, BarState.Parse("menu", null, null, null, music: "radio"));
            Assert.Equal(BarState.Wanted, BarState.Parse(null, null, null, null, wanted: "board", music: "radio"));
        }

        [Theory]
        [InlineData("playing", "Radio gra")]
        [InlineData("loading", "Łączenie ze stacją…")]
        [InlineData("paused", "Radio zatrzymane")]
        [InlineData("idle", "Radio zatrzymane")]
        [InlineData("error", "Ta stacja jest chwilowo niedostępna.")]
        public void StatusText_ForScreenReaders(string status, string text) => Assert.Equal(text, MusicState.TextFor(status));
    }
}
