using System.Net;
using SansPost.Features.Posts;
using SansPost.Features.Reactions;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Social
{
    // Szybkie testy kontraktu reakcji (pełny pipeline, SQLite). Współbieżność — w testach PostgreSQL.
    public class ReactionsApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public ReactionsApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private int LikeRows(int postId) => _factory.WithScope(db => db.Likes.Count(l => l.PostId == postId));

        [Fact]
        public async Task Put_CreatesLike_AndRepeatedPutKeepsExactlyOne()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();

            var first = await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            var second = await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            var third = await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));

            Assert.All(new[] { first, second, third }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var summary = await third.ReadAsync<LikeSummaryResponse>();
            Assert.Equal(1, summary.LikeCount);
            Assert.True(summary.LikedByCurrentUser);
            Assert.Equal(1, LikeRows(post.Id));
        }

        [Fact]
        public async Task Delete_RemovesLike_AndRepeatedDeleteIsSafe()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));

            var first = await client.SendAsync(Request(HttpMethod.Delete, $"/api/posts/{post.Id}/like"));
            var second = await client.SendAsync(Request(HttpMethod.Delete, $"/api/posts/{post.Id}/like"));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var summary = await second.ReadAsync<LikeSummaryResponse>();
            Assert.Equal(0, summary.LikeCount);
            Assert.False(summary.LikedByCurrentUser);
            Assert.Equal(0, LikeRows(post.Id));
        }

        [Fact]
        public async Task TwoDifferentUsers_CanBothLikeSamePost()
        {
            var alice = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("a"));
            var bob = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("b"));
            var post = await alice.CreatePostAsync();

            await alice.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            var bobLike = await bob.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));

            Assert.Equal(2, (await bobLike.ReadAsync<LikeSummaryResponse>()).LikeCount);
            Assert.Equal(2, LikeRows(post.Id));
        }

        [Fact]
        public async Task Summary_IsPublic_AndLikedByCurrentUserIsFalseForAnonymous()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));

            var anonymous = await (await _factory.CreateHttpsClient().GetAsync($"/api/posts/{post.Id}/likes")).ReadAsync<LikeSummaryResponse>();
            var owner = await (await client.GetAsync($"/api/posts/{post.Id}/likes")).ReadAsync<LikeSummaryResponse>();

            Assert.Equal(1, anonymous.LikeCount);
            Assert.False(anonymous.LikedByCurrentUser);
            Assert.True(owner.LikedByCurrentUser);
        }

        [Fact]
        public async Task Like_OnMissingPost_Returns404_AndAnonymousReturns401()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Put, "/api/posts/999999/like"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateHttpsClient().SendAsync(Request(HttpMethod.Put, "/api/posts/1/like"))).StatusCode);
        }

        [Fact]
        public async Task ToggleEndpoint_NoLongerExists()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();

            var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/posts/{post.Id}/likes/toggle"));

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, LikeRows(post.Id));
        }

        [Fact]
        // Sprint 6: DELETE posta to soft delete — polubienia i komentarze znikają publicznie razem z postem,
        // ale dane zostają (historia dla moderacji). Wcześniej: fizyczne DELETE + FK CASCADE.
        public async Task DeletingPost_HidesItsLikesAndCommentsPublicly_ButRetainsData()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            await client.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            var (comment, _) = await client.CreateCommentAsync(post.Id);

            var etag = (await client.GetAsync($"/api/posts/{post.Id}")).Headers.ETag;
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, $"/api/posts/{post.Id}", etag))).StatusCode);

            var anonymous = _factory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/posts/{post.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/posts/{post.Id}/likes")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/posts/{post.Id}/comments")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/comments/{comment.Id}")).StatusCode);

            var stored = _factory.WithScope(db => db.Posts.Single(p => p.Id == post.Id));
            Assert.Equal(SansPost.Features.ContentStatus.Deleted, stored.Status);
            Assert.NotNull(stored.DeletedAt);
            Assert.Equal(1, LikeRows(post.Id));
            Assert.Equal(1, _factory.WithScope(db => db.Comments.Count(c => c.PostId == post.Id)));
        }
    }
}
