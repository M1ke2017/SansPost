using SansPost.Features;

namespace SansPost.Tests.Social
{
    public class KeysetCursorTests
    {
        [Fact]
        public void Cursor_RoundTrips()
        {
            var original = new KeysetCursor("comments", new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560), 42);

            Assert.True(KeysetCursor.TryDecode(original.Encode(), "comments", out var decoded));
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void RankedCursor_RoundTripsScoreAndAsOf()
        {
            var asOf = new DateTime(2026, 9, 23, 18, 30, 0, DateTimeKind.Utc);
            var original = new KeysetCursor("posts.Popular", asOf.AddHours(-3), 7, Rank: -12_345, AsOf: asOf);

            Assert.True(KeysetCursor.TryDecode(original.Encode(), "posts.Popular", out var decoded));
            Assert.Equal(original, decoded);
        }

        [Theory]
        [InlineData("posts.Newest", "posts.Oldest")]
        [InlineData("posts.Newest", "comments")]
        public void Cursor_IsRejected_ForDifferentScope(string encodedScope, string expectedScope)
        {
            var cursor = new KeysetCursor(encodedScope, DateTime.UtcNow, 1).Encode();

            Assert.False(KeysetCursor.TryDecode(cursor, expectedScope, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("%%%")]
        [InlineData("bm90LWEtY3Vyc29y")]  // "not-a-cursor"
        [InlineData("Y29tbWVudHN8LTF8MQ")] // "comments|-1|1" — ujemne ticki
        public void MalformedCursor_IsRejected(string value)
        {
            Assert.False(KeysetCursor.TryDecode(value, "comments", out _));
        }
    }
}
