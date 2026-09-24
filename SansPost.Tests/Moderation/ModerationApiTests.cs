using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Moderation
{
    // Zgłoszenia i moderacja treści przez pełny pipeline HTTP (SQLite). Współbieżność — testy PostgreSQL.
    [Trait("Category", "Moderation")]
    public class ModerationApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public ModerationApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private Task<HttpClient> AdminAsync() => _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("adm"), SansPost.Features.Identity.UserRole.Admin);

        private static Task<HttpResponseMessage> ReportAsync(HttpClient client, string targetType, int targetId, string reason = "Spam") =>
            client.PostAsJsonAsync("/api/reports", new { targetType, targetId, reason, details = "Szczegóły" });

        // G
        [Fact]
        public async Task Anonymous_CannotReport()
        {
            var response = await ReportAsync(_factory.CreateHttpsClient(), "Post", 1);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // H, I — zgłoszenie posta i komentarza; ponowienie tego samego zgłoszenia jest idempotentne.
        [Fact]
        public async Task ActiveUser_CanReportPostAndComment_AndRepeatIsIdempotent()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("au"));
            var reporter = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("re"));
            var post = await author.CreatePostAsync();
            var (comment, _) = await author.CreateCommentAsync(post.Id);

            var postReport = await ReportAsync(reporter, "Post", post.Id);
            var repeated = await ReportAsync(reporter, "Post", post.Id, "Abuse");
            var commentReport = await ReportAsync(reporter, "Comment", comment.Id, "Harassment");

            Assert.Equal(HttpStatusCode.Created, postReport.StatusCode);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal((await postReport.ReadAsync<ReportResponse>()).Id, (await repeated.ReadAsync<ReportResponse>()).Id);
            Assert.Equal(HttpStatusCode.Created, commentReport.StatusCode);
            Assert.Equal(ReportStatus.Pending, (await commentReport.ReadAsync<ReportResponse>()).Status);
        }

        // L — nieistniejący (lub niepubliczny) cel.
        [Theory]
        [InlineData("Post", 999_999)]
        [InlineData("Comment", 999_999)]
        public async Task Report_OfNonexistentTarget_Returns404(string targetType, int targetId)
        {
            var reporter = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(reporter, targetType, targetId)).StatusCode);
        }

        [Fact]
        public async Task Report_RejectsUnknownReasonAndTooLongDetails()
        {
            var reporter = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await reporter.CreatePostAsync();

            var badReason = await ReportAsync(reporter, "Post", post.Id, "NotAReason");
            var tooLong = await reporter.PostAsJsonAsync("/api/reports", new { targetType = "Post", targetId = post.Id, reason = "Spam", details = new string('x', 501) });

            Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        }

        // M — zwykły użytkownik nie ma dostępu do żadnego endpointu moderacji.
        [Theory]
        [InlineData("GET", "/api/moderation/reports")]
        [InlineData("GET", "/api/moderation/actions")]
        [InlineData("GET", "/api/moderation/posts/1")]
        [InlineData("POST", "/api/moderation/posts/1/hide")]
        [InlineData("POST", "/api/moderation/comments/1/restore")]
        [InlineData("POST", "/api/moderation/users/1/ban")]
        [InlineData("POST", "/api/moderation/reports/1/dismiss")]
        public async Task User_CannotAccessModeration(string method, string url)
        {
            var user = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            var response = await user.SendAsync(Request(new HttpMethod(method), url, body: method == "POST" ? new { } : null));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // N, O, Q, U — admin widzi kolejkę; ukrycie posta: znika z feedu, profilu, kategorii i GET; zgłoszenie rozstrzygnięte; audyt.
        [Fact]
        public async Task HidePost_RemovesItFromEveryPublicReadModel_ResolvesReports_AndAudits()
        {
            var authorName = TestUsers.UniqueName("hp");
            var author = await _factory.CreateAuthenticatedApiClientAsync(authorName);
            var reporter = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("rp"));
            var admin = await AdminAsync();
            var post = await author.CreatePostAsync("Do ukrycia");
            var report = await (await ReportAsync(reporter, "Post", post.Id)).ReadAsync<ReportResponse>();

            var queue = await (await admin.GetAsync("/api/moderation/reports?limit=50")).ReadAsync<KeysetPage<ModerationQueueItem>>();
            Assert.Contains(queue.Items, i => i.ReportId == report.Id && i.TargetPreview == "Do ukrycia" && i.TargetAuthorUsername == authorName);

            var hide = await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/hide", new { reason = "Spam" });
            Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);

            var anonymous = _factory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/posts/{post.Id}")).StatusCode);
            Assert.DoesNotContain((await (await anonymous.GetAsync("/api/posts?limit=50")).ReadAsync<KeysetPage<PostSummaryResponse>>()).Items, p => p.Id == post.Id);
            Assert.Empty((await (await anonymous.GetAsync($"/api/users/{post.AuthorId}/posts")).ReadAsync<KeysetPage<PostSummaryResponse>>()).Items);

            var profile = await (await anonymous.GetAsync($"/api/profiles/{authorName}")).ReadAsync<PublicProfileResponse>();
            Assert.Equal(0, profile.PostCount);
            Assert.Empty(profile.RecentPosts);

            // Moderator nadal widzi treść (Hidden).
            var view = await (await admin.GetAsync($"/api/moderation/posts/{post.Id}")).ReadAsync<ModeratedContentView>();
            Assert.Equal(ContentStatus.Hidden, view.Status);

            // Atomowo: zgłoszenie rozstrzygnięte + wpis audytu.
            Assert.Equal(ReportStatus.Resolved, _factory.WithScope(db => db.Reports.Single(r => r.Id == report.Id).Status));
            var actions = await (await admin.GetAsync("/api/moderation/actions?limit=50")).ReadAsync<KeysetPage<ModerationActionResponse>>();
            Assert.Contains(actions.Items, a => a.ActionType == ModerationActionType.HidePost && a.TargetId == post.Id && a.Reason == "Spam");
        }

        // R — ukryty komentarz znika z listy, liczników i aktywności na profilu.
        [Fact]
        public async Task HideComment_RemovesItFromCommentsCountsAndProfileActivity()
        {
            var commenterName = TestUsers.UniqueName("hc");
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("pa"));
            var commenter = await _factory.CreateAuthenticatedApiClientAsync(commenterName);
            var admin = await AdminAsync();
            var post = await author.CreatePostAsync();
            var (comment, _) = await commenter.CreateCommentAsync(post.Id, "Obraźliwy");

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/comments/{comment.Id}/hide", new { })).StatusCode);

            var anonymous = _factory.CreateHttpsClient();
            Assert.Empty((await (await anonymous.GetAsync($"/api/posts/{post.Id}/comments")).ReadAsync<KeysetPage<SansPost.Features.Comments.CommentResponse>>()).Items);
            Assert.Equal(0, (await (await anonymous.GetAsync($"/api/posts/{post.Id}")).ReadAsync<PostDetailsResponse>()).CommentCount);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/comments/{comment.Id}")).StatusCode);

            var profile = await (await anonymous.GetAsync($"/api/profiles/{commenterName}")).ReadAsync<PublicProfileResponse>();
            Assert.Equal(0, profile.CommentCount);
            Assert.Empty(profile.RecentComments);

            // Autor komentarza nie może edytować ukrytego komentarza (dla niego też 404).
            var edit = await commenter.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", new System.Net.Http.Headers.EntityTagHeaderValue("\"2\""), new { content = "Poprawiony" }));
            Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        }

        // S — przywrócenie ukrytej treści.
        [Fact]
        public async Task RestoreHiddenPost_MakesItPublicAgain()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var admin = await AdminAsync();
            var post = await author.CreatePostAsync();

            await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/hide", new { });
            var restore = await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/restore", new { reason = "Pomyłka" });

            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _factory.CreateHttpsClient().GetAsync($"/api/posts/{post.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/restore", new { })).StatusCode);
        }

        // T — treść usunięta przez autora nie jest przywracana ani ukrywana przez zwykłą moderację.
        [Fact]
        public async Task DeletedByAuthor_CannotBeRestoredOrHiddenByModeration()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var admin = await AdminAsync();
            var post = await author.CreatePostAsync();
            var etag = (await author.GetAsync($"/api/posts/{post.Id}")).Headers.ETag;
            await author.SendAsync(Request(HttpMethod.Delete, $"/api/posts/{post.Id}", etag));

            var restore = await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/restore", new { });
            var hide = await admin.PostAsJsonAsync($"/api/moderation/posts/{post.Id}/hide", new { });

            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            Assert.Equal("content-deleted-by-author", (await restore.Content.ReadFromJsonAsync<ProblemDetails>())!.Extensions["code"]!.ToString());
            Assert.Equal(HttpStatusCode.Conflict, hide.StatusCode);
            Assert.Equal(ContentStatus.Deleted, (await (await admin.GetAsync($"/api/moderation/posts/{post.Id}")).ReadAsync<ModeratedContentView>()).Status);
        }

        [Fact]
        public async Task DismissReport_ClosesItWithAudit_AndSecondDismissConflicts()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var reporter = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var admin = await AdminAsync();
            var post = await author.CreatePostAsync();
            var report = await (await ReportAsync(reporter, "Post", post.Id)).ReadAsync<ReportResponse>();

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/reports/{report.Id}/dismiss", new { reason = "Nie narusza zasad" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/moderation/reports/{report.Id}/dismiss", new { })).StatusCode);

            Assert.Equal(ReportStatus.Dismissed, _factory.WithScope(db => db.Reports.Single(r => r.Id == report.Id).Status));
            Assert.Equal(HttpStatusCode.OK, (await _factory.CreateHttpsClient().GetAsync($"/api/posts/{post.Id}")).StatusCode);
            Assert.Equal(1, _factory.WithScope(db => db.ModerationActions.Count(a => a.ActionType == ModerationActionType.DismissReport && a.TargetId == report.Id)));
        }

        // Część L (plain text): HTML w treści jest renderowany jako tekst, nie jako markup.
        [Fact]
        public async Task HtmlInContent_IsRenderedAsPlainText()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await author.PostAsJsonAsync("/api/posts", new { title = "Test XSS", content = "<script>alert('xss')</script><b>pogrubienie</b>", category = "General" });
            var id = (await post.ReadAsync<PostDetailsResponse>()).Id;

            var html = await _factory.CreateHttpsClient().GetStringAsync($"/post-view/{id}");

            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>alert", html);
            Assert.DoesNotContain("<b>pogrubienie</b>", html);
        }
    }
}
