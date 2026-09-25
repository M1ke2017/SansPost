using System.Net;
using System.Net.Http.Json;
using SansPost.Features;
using SansPost.Features.Notifications;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Notifications
{
    // Notifications API przez pełny pipeline (JWT, polityki) + przepływ end-to-end na poziomie HTTP
    // (REST dla akcji, cookie + prerender Blazor dla nagłówka z dzwonkiem).
    [Trait("Category", "Notifications")]
    public class NotificationApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public NotificationApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private async Task<(HttpClient A, HttpClient B, string AName, string BName, PostDetailsLite Post, int CommentId)> ArrangeCommentAsync()
        {
            var aName = TestUsers.UniqueName("a");
            var bName = TestUsers.UniqueName("b");
            var a = await _factory.CreateAuthenticatedApiClientAsync(aName);
            var b = await _factory.CreateAuthenticatedApiClientAsync(bName);

            var post = await a.CreatePostAsync("Post do powiadomienia");
            var comment = await b.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content = "Świetny post!" });
            Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
            var commentId = (await comment.Content.ReadFromJsonAsync<IdOnly>())!.Id;
            return (a, b, aName, bName, new PostDetailsLite(post.Id, post.Title), commentId);
        }

        private static async Task<KeysetPage<NotificationResponse>> ListAsync(HttpClient client, string query = "") =>
            (await client.GetFromJsonAsync<KeysetPage<NotificationResponse>>("/api/notifications" + query, SansPostFactory.Json))!;

        private static async Task<int> UnreadAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<UnreadCountResponse>("/api/notifications/unread-count", SansPostFactory.Json))!.Count;

        [Fact]
        public async Task Endpoints_RequireAuthentication()
        {
            var anonymous = _factory.CreateHttpsClient();

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notifications/read-all", null)).StatusCode);
        }

        // 4, 5 — tożsamość wyłącznie z JWT: parametr userId w zapytaniu jest ignorowany, cudze powiadomienie = 404.
        [Fact]
        public async Task OtherUser_CannotReadOrMarkRecipientsNotifications()
        {
            var (a, b, _, _, _, _) = await ArrangeCommentAsync();
            var id = (await ListAsync(a)).Items.Single().Id;
            var aUserId = _factory.WithScope(db => db.Notifications.Single(n => n.Id == id).UserId);

            Assert.Empty((await ListAsync(b, $"?userId={aUserId}")).Items);
            Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsync($"/api/notifications/{id}/read", null)).StatusCode);
            Assert.Equal(1, await UnreadAsync(a));
        }

        // 6–9 przez API + 11: JSON bez danych uwierzytelnienia.
        [Fact]
        public async Task UnreadCount_MarkRead_ReadAll_OverHttp()
        {
            var (a, b, _, bName, post, _) = await ArrangeCommentAsync();
            await b.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content = "Jeszcze jedno" });
            Assert.Equal(2, await UnreadAsync(a));

            var json = await a.GetStringAsync("/api/notifications");
            foreach (var forbidden in new[] { "email", "passwordHash", "authVersion", "Świetny post" })
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(bName, json);

            var id = (await ListAsync(a)).Items.First().Id;
            var first = await a.PatchAsync($"/api/notifications/{id}/read", null);
            var second = await a.PatchAsync($"/api/notifications/{id}/read", null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(1, await UnreadAsync(a));

            var readAll = await (await a.PostAsync("/api/notifications/read-all", null)).Content.ReadFromJsonAsync<MarkAllReadResponse>();
            Assert.Equal(1, readAll!.Updated);
            Assert.Equal(0, await UnreadAsync(a));
        }

        // E2E (HTTP): A publikuje, B komentuje, A widzi licznik w nagłówku (prerender), powiadomienie prowadzi do
        // właściwego posta i komentarza, po odczycie licznik znika.
        [Fact]
        public async Task EndToEnd_CommentNotification_FromBadgeToPost()
        {
            var (aApi, _, aName, bName, post, commentId) = await ArrangeCommentAsync();

            var browser = _factory.CreateHttpsClient();
            var token = SansPostFactory.ExtractAntiforgeryToken(await browser.GetStringAsync("/login"));
            var login = await browser.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = $"{aName}@example.com",
                ["Password"] = TestUsers.Password,
                ["ReturnUrl"] = "/saloon"
            }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

            var home = await browser.GetStringAsync("/saloon");
            Assert.Contains("<span class=\"notif-badge\" aria-hidden=\"true\">1</span>", home);

            var notification = (await ListAsync(aApi)).Items.Single();
            Assert.Equal((bName, post.Id, commentId, post.Title), (notification.ActorUsername, notification.PostId, notification.CommentId, notification.PostTitle));

            // Cel kliknięcia: /post-view/{postId}#comment-{commentId} — post i kotwica komentarza istnieją.
            var postPage = await browser.GetStringAsync($"/post-view/{notification.PostId}");
            Assert.Contains($"id=\"comment-{commentId}\"", postPage);

            Assert.Equal(HttpStatusCode.OK, (await aApi.PatchAsync($"/api/notifications/{notification.Id}/read", null)).StatusCode);
            Assert.DoesNotContain("class=\"notif-badge\"", await browser.GetStringAsync("/saloon"));
        }

        private sealed record IdOnly(int Id);

        private sealed record PostDetailsLite(int Id, string Title);
    }
}
