using System.Net;
using System.Text.Json;
using SansPost.Features;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Social
{
    public class ProfilesAndFeedCountsApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public ProfilesAndFeedCountsApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task PublicProfile_ReturnsOnlySafeFields()
        {
            var username = TestUsers.UniqueName("Prof");
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);
            await client.CreatePostAsync();

            var response = await _factory.CreateHttpsClient().GetAsync($"/api/profiles/{username}");
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(json);
            var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                new[] { "userId", "username", "joinedAt", "postCount", "commentCount", "recentPosts", "recentComments" }.ToHashSet(StringComparer.OrdinalIgnoreCase),
                fields);
            foreach (var forbidden in new[] { "email", "passwordHash", "normalized", "role", "subscription", "refresh", "@example.com", "$2" })
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PublicProfile_CountsAndRecentActivityAreCorrect()
        {
            var username = TestUsers.UniqueName("Cnt");
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);
            var other = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("o"));

            var first = await client.CreatePostAsync("Pierwszy");
            var second = await client.CreatePostAsync("Drugi");
            var foreignPost = await other.CreatePostAsync("Cudzy");
            await client.CreateCommentAsync(first.Id, "Mój komentarz 1");
            await client.CreateCommentAsync(foreignPost.Id, "Mój komentarz 2");
            await other.CreateCommentAsync(first.Id, "Nie mój");

            // Nazwa bez względu na wielkość liter (NormalizedUsername).
            var profile = await (await _factory.CreateHttpsClient().GetAsync($"/api/profiles/{username.ToUpperInvariant()}")).ReadAsync<PublicProfileResponse>();

            Assert.Equal(username, profile.Username);
            Assert.Equal(2, profile.PostCount);
            Assert.Equal(2, profile.CommentCount);
            Assert.Equal(new[] { second.Id, first.Id }, profile.RecentPosts.Select(p => p.Id));
            Assert.Equal(new[] { "Mój komentarz 2", "Mój komentarz 1" }, profile.RecentComments.Select(c => c.ContentPreview));
            Assert.Equal("Cudzy", profile.RecentComments[0].PostTitle);
        }

        [Fact]
        public async Task UnknownProfile_Returns404()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateHttpsClient().GetAsync("/api/profiles/nobody_here_123")).StatusCode);
        }

        [Fact]
        public async Task FeedAndDetails_ContainCorrectCounts_AndViewerSpecificLikeState()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("a"));
            var fan = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("f"));
            var post = await author.CreatePostAsync("Z licznikami");

            await author.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            await fan.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));
            await fan.CreateCommentAsync(post.Id, "1");
            await fan.CreateCommentAsync(post.Id, "2");
            await author.CreateCommentAsync(post.Id, "3");

            var anonymousFeed = await (await _factory.CreateHttpsClient().GetAsync($"/api/users/{post.AuthorId}/posts")).ReadAsync<KeysetPage<PostSummaryResponse>>();
            var fanDetails = await (await fan.GetAsync($"/api/posts/{post.Id}")).ReadAsync<PostDetailsResponse>();
            var strangerDetails = await (await (await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("s"))).GetAsync($"/api/posts/{post.Id}")).ReadAsync<PostDetailsResponse>();

            var item = anonymousFeed.Items.Single(p => p.Id == post.Id);
            Assert.Equal(2, item.LikeCount);
            Assert.Equal(3, item.CommentCount);
            Assert.False(item.LikedByCurrentUser);
            Assert.Equal((2, 3, true), (fanDetails.LikeCount, fanDetails.CommentCount, fanDetails.LikedByCurrentUser));
            Assert.False(strangerDetails.LikedByCurrentUser);
        }
    }
}
