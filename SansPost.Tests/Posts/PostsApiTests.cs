using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Posts
{
    // Szybkie testy kontraktu HTTP Posts (pełny pipeline, SQLite).
    public class PostsApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public PostsApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private static object Body(string title, string category = "General", string content = "Treść posta") =>
            new { title, content, category };

        private static async Task<PostDetailsResponse> ReadPostAsync(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<PostDetailsResponse>(SansPostFactory.Json))!;

        private static async Task<PostFeedResponse> ReadFeedAsync(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<PostFeedResponse>(SansPostFactory.Json))!;

        private static HttpRequestMessage Put(int postId, object body, string? ifMatch)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/posts/{postId}") { Content = JsonContent.Create(body) };
            if (ifMatch is not null)
                request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
            return request;
        }

        [Fact]
        public async Task CreateThenGet_ReturnsPostWithETagAndNoUpdatedAt()
        {
            var username = TestUsers.UniqueName();
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);

            var created = await client.PostAsJsonAsync("/api/posts", Body("Pierwszy post", "Games"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("\"1\"", created.Headers.ETag!.Tag);

            var get = await _factory.CreateHttpsClient().GetAsync(created.Headers.Location);
            var post = await ReadPostAsync(get);

            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("\"1\"", get.Headers.ETag!.Tag);
            Assert.Equal(PostCategory.Games, post.Category);
            Assert.Equal(username, post.AuthorUsername);
            Assert.Null(post.UpdatedAt);
        }

        [Theory]
        [InlineData("ab", "Treść")]                     // tytuł < 3
        [InlineData("   ", "Treść")]                    // sam whitespace
        [InlineData("Poprawny tytuł", "   ")]           // pusta treść
        public async Task Create_RejectsInvalidInput(string title, string content)
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            var response = await client.PostAsJsonAsync("/api/posts", new { title, content, category = "General" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Create_RejectsTooLongTitleAndUnknownCategory()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            var longTitle = await client.PostAsJsonAsync("/api/posts", Body(new string('x', PostLimits.TitleMaxLength + 1)));
            var badCategory = await client.PostAsJsonAsync("/api/posts", Body("Tytuł", "Sport"));

            Assert.Equal(HttpStatusCode.BadRequest, longTitle.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, badCategory.StatusCode);
        }

        [Fact]
        public async Task Feed_PaginatesWithCursor_InStableNewestFirstOrder()
        {
            var username = TestUsers.UniqueName();
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);
            var createdIds = new List<int>();
            for (var i = 0; i < 5; i++)
                createdIds.Add((await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body($"Feed {i}")))).Id);

            var authorId = (await ReadPostAsync(await client.GetAsync($"/api/posts/{createdIds[0]}"))).AuthorId;
            var collected = new List<int>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var url = $"/api/users/{authorId}/posts?limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
                var feed = await ReadFeedAsync(await _factory.CreateHttpsClient().GetAsync(url));
                collected.AddRange(feed.Items.Select(p => p.Id));
                Assert.Equal(feed.NextCursor is not null, feed.HasMore);
                cursor = feed.NextCursor;
                pages++;
            }
            while (cursor is not null);

            Assert.Equal(3, pages);
            Assert.Equal(Enumerable.Reverse(createdIds), collected);
        }

        [Fact]
        public async Task Feed_SupportsOldestSortAndCategoryFilter()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var travel = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Podróż", "Travel")));
            await client.PostAsJsonAsync("/api/posts", Body("Pomysł", "Ideas"));

            var byCategory = await ReadFeedAsync(await client.GetAsync("/api/posts?category=Travel&limit=50"));
            var oldest = await ReadFeedAsync(await client.GetAsync($"/api/users/{travel.AuthorId}/posts?sort=Oldest"));

            Assert.All(byCategory.Items, p => Assert.Equal(PostCategory.Travel, p.Category));
            Assert.Contains(byCategory.Items, p => p.Id == travel.Id);
            Assert.Equal(travel.Id, oldest.Items[0].Id);
        }

        [Fact]
        public async Task Feed_ReturnsPreviewInsteadOfFullContent()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Długi", content: new string('a', 1000))));

            var feed = await ReadFeedAsync(await client.GetAsync($"/api/users/{post.AuthorId}/posts"));

            Assert.Equal(PostLimits.PreviewLength, feed.Items[0].ContentPreview.Length);
            Assert.True(feed.Items[0].IsContentTruncated);
        }

        [Theory]
        [InlineData("?cursor=not-a-cursor")]
        [InlineData("?limit=0")]
        [InlineData("?limit=51")]
        public async Task Feed_RejectsInvalidParameters(string query)
        {
            var response = await _factory.CreateHttpsClient().GetAsync("/api/posts" + query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Update_WithCurrentETag_Succeeds_AndStaleETagReturns412()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var created = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Przed edycją")));

            var update = await client.SendAsync(Put(created.Id, Body("Po edycji"), "\"1\""));
            var updated = await ReadPostAsync(update);

            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal("\"2\"", update.Headers.ETag!.Tag);
            Assert.Equal(2, updated.Version);
            Assert.NotNull(updated.UpdatedAt);
            Assert.Equal(created.CreatedAt, updated.CreatedAt);

            var stale = await client.SendAsync(Put(created.Id, Body("Stara wersja"), "\"1\""));

            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.NotNull(await stale.Content.ReadFromJsonAsync<ProblemDetails>());
            Assert.Equal("Po edycji", (await ReadPostAsync(await client.GetAsync($"/api/posts/{created.Id}"))).Title);
        }

        [Fact]
        public async Task Update_WithoutIfMatch_Returns428_AndWithMalformedIfMatch_Returns400()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var created = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Tytuł")));

            var missing = await client.SendAsync(Put(created.Id, Body("Nowy tytuł"), null));
            var wildcardRequest = Put(created.Id, Body("Nowy tytuł"), null);
            wildcardRequest.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
            var wildcard = await client.SendAsync(wildcardRequest);

            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, wildcard.StatusCode);
        }

        [Fact]
        public async Task Update_WithoutRealChange_KeepsVersionAndUpdatedAtNull()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var created = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Bez zmian")));

            var response = await client.SendAsync(Put(created.Id, Body("Bez zmian"), "\"1\""));
            var post = await ReadPostAsync(response);

            Assert.Equal(1, post.Version);
            Assert.Null(post.UpdatedAt);
        }

        [Fact]
        public async Task Delete_RemovesPost_AndStaleUpdateThenReturns404()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var created = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Do usunięcia")));

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/posts/{created.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/posts/{created.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Put(created.Id, Body("Wskrzeszenie"), "\"1\""))).StatusCode);
        }

        [Fact]
        public async Task Delete_WithStaleIfMatch_Returns412()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var created = await ReadPostAsync(await client.PostAsJsonAsync("/api/posts", Body("Wersjonowany")));
            await client.SendAsync(Put(created.Id, Body("Zmieniony"), "\"1\""));

            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/posts/{created.Id}");
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse("\"1\""));

            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(request)).StatusCode);
        }

        [Fact]
        public async Task FreeUser_CannotExceedPostLimit()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            for (var i = 0; i < PostLimits.FreePostLimit; i++)
                Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/posts", Body($"Post {i}"))).StatusCode);

            var overLimit = await client.PostAsJsonAsync("/api/posts", Body("Jedenasty"));
            var quota = await client.GetFromJsonAsync<PostQuotaResponse>("/api/posts/quota");

            Assert.Equal(HttpStatusCode.Forbidden, overLimit.StatusCode);
            Assert.Equal(new PostQuotaResponse(10, 10, 0), quota);
        }
    }
}
