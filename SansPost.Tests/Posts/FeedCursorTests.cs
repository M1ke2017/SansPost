using SansPost.Features.Posts;

namespace SansPost.Tests.Posts
{
    public class FeedCursorTests
    {
        [Fact]
        public void Cursor_RoundTrips()
        {
            var original = new FeedCursor(PostSort.Newest, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560), 42);

            Assert.True(FeedCursor.TryDecode(original.Encode(), PostSort.Newest, out var decoded));
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Cursor_IsRejected_ForDifferentSortOrder()
        {
            var cursor = new FeedCursor(PostSort.Newest, DateTime.UtcNow, 1).Encode();

            Assert.False(FeedCursor.TryDecode(cursor, PostSort.Oldest, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("%%%")]
        [InlineData("bm90LWEtY3Vyc29y")]  // "not-a-cursor"
        [InlineData("MHwtMXwx")]           // ujemne ticki
        public void MalformedCursor_IsRejected(string value)
        {
            Assert.False(FeedCursor.TryDecode(value, PostSort.Newest, out _));
        }
    }
}
