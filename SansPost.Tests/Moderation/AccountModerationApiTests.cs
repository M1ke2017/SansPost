using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Moderation
{
    // Zawieszenie, ban, zmiana roli: egzekwowanie po stronie serwera i unieważnianie istniejących sesji (AuthVersion).
    [Trait("Category", "Moderation")]
    public class AccountModerationApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public AccountModerationApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private int IdOf(string username) => _factory.WithScope(db => db.Users.Single(u => u.Username == username).Id);

        private Task<HttpClient> AdminAsync() => _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("adm"), UserRole.Admin);

        private static string? CodeOf(ProblemDetails? problem) => problem?.Extensions.TryGetValue("code", out var code) == true ? code?.ToString() : null;

        private async Task<HttpClient> LoginAgainAsync(string username)
        {
            var client = _factory.CreateHttpsClient();
            var tokens = await _factory.LoginApiAsync(client, username);
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
            return client;
        }

        // W — zawieszony: stara sesja odrzucona; po ponownym logowaniu może czytać, ale każdy zapis → 403.
        [Fact]
        public async Task SuspendedUser_CanRead_ButEveryWriteIsDenied()
        {
            var username = TestUsers.UniqueName("su");
            var user = await _factory.CreateAuthenticatedApiClientAsync(username);
            var other = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("ot"));
            var admin = await AdminAsync();
            var foreignPost = await other.CreatePostAsync();

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/users/{IdOf(username)}/suspend", new { reason = "Spam" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);

            var suspended = await LoginAgainAsync(username);
            Assert.Equal(HttpStatusCode.OK, (await suspended.GetAsync("/api/posts")).StatusCode);

            var writes = new[]
            {
                await suspended.PostAsJsonAsync("/api/posts", new { title = "Nowy post", content = "x", category = "General" }),
                await suspended.PostAsJsonAsync($"/api/posts/{foreignPost.Id}/comments", new { content = "x" }),
                await suspended.SendAsync(Request(HttpMethod.Put, $"/api/posts/{foreignPost.Id}/like")),
                await suspended.PostAsJsonAsync("/api/reports", new { targetType = "Post", targetId = foreignPost.Id, reason = "Spam" }) // K
            };

            foreach (var response in writes)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("account-suspended", CodeOf(await response.Content.ReadFromJsonAsync<ProblemDetails>()));
            }
        }

        // X, Y, Z — ban: logowanie odrzucone, refresh odrzucony, wszystkie refresh tokeny unieważnione, stary access token martwy.
        [Fact]
        public async Task BannedUser_CannotLoginOrRefresh_AndExistingSessionsDie()
        {
            var username = TestUsers.UniqueName("bn");
            await _factory.CreateUserAsync(username);
            var client = _factory.CreateHttpsClient();
            var tokens = await _factory.LoginApiAsync(client, username);
            var secondSession = await _factory.LoginApiAsync(_factory.CreateHttpsClient(), username);
            var admin = await AdminAsync();
            var userId = IdOf(username);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/users/{userId}/ban", new { reason = "Nadużycia" })).StatusCode);

            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

            var login = await _factory.CreateHttpsClient().PostAsJsonAsync("/api/auth/login", new { email = $"{username}@example.com", password = TestUsers.Password });
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

            var refresh = await _factory.CreateHttpsClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = secondSession.RefreshToken });
            Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

            Assert.All(_factory.WithScope(db => db.RefreshTokens.Where(t => t.UserId == userId).ToList()), t => Assert.NotNull(t.RevokedAt));
        }

        [Fact]
        public async Task ReactivatedUser_CanLoginAndWriteAgain()
        {
            var username = TestUsers.UniqueName("ra");
            await _factory.CreateUserAsync(username);
            var admin = await AdminAsync();
            var userId = IdOf(username);

            await admin.PostAsJsonAsync($"/api/moderation/users/{userId}/ban", new { });
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/users/{userId}/reactivate", new { })).StatusCode);

            var user = await LoginAgainAsync(username);
            Assert.Equal(HttpStatusCode.Created, (await user.PostAsJsonAsync("/api/posts", new { title = "Wróciłem", content = "x", category = "General" })).StatusCode);
        }

        // AA — istniejące cookie UI traci ważność po zmianie AuthVersion (bez czekania na wygaśnięcie cookie).
        [Fact]
        public async Task ExistingCookieSession_IsRejected_AfterSuspension()
        {
            var username = TestUsers.UniqueName("ck");
            await _factory.CreateUserAsync(username);
            var browser = _factory.CreateHttpsClient();
            var token = SansPostFactory.ExtractAntiforgeryToken(await browser.GetStringAsync("/login"));
            await browser.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = $"{username}@example.com",
                ["Password"] = TestUsers.Password
            }));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/user-panel")).StatusCode);

            var admin = await AdminAsync();
            await admin.PostAsJsonAsync($"/api/moderation/users/{IdOf(username)}/suspend", new { });

            var afterSuspension = await browser.GetAsync("/user-panel");
            Assert.Equal(HttpStatusCode.Redirect, afterSuspension.StatusCode);
            Assert.StartsWith("/login", afterSuspension.Headers.Location!.OriginalString.Replace("https://localhost", ""));
        }

        // Sprint 10 (A4) — unieważniona sesja na publicznej stronie: zamiast cichego trybu gościa przekierowanie
        // z komunikatem ("sesja zakończona") i powrotem na tę stronę; cookie usunięte, kolejne wejście = zwykły gość.
        [Fact]
        public async Task InvalidatedCookie_OnPublicPage_RedirectsToLoginWithSessionEndedMessage()
        {
            var username = TestUsers.UniqueName("se");
            await _factory.CreateUserAsync(username);
            var browser = _factory.CreateHttpsClient();
            var token = SansPostFactory.ExtractAntiforgeryToken(await browser.GetStringAsync("/login"));
            await browser.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = $"{username}@example.com",
                ["Password"] = TestUsers.Password
            }));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/categories")).StatusCode);

            var admin = await AdminAsync();
            await admin.PostAsJsonAsync($"/api/moderation/users/{IdOf(username)}/suspend", new { });

            var ended = await browser.GetAsync("/categories?x=1");
            Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);
            Assert.Equal("/login?ended=1&returnUrl=%2Fcategories%3Fx%3D1", ended.Headers.Location!.OriginalString);
            Assert.Contains(ended.Headers.GetValues("Set-Cookie"), c => c.StartsWith("SansPost.Auth=;", StringComparison.Ordinal));

            var login = await browser.GetStringAsync("/login?ended=1");
            Assert.Contains("Twoja sesja została zakończona.", login);

            // Cookie już usunięte — publiczna strona jak dla gościa, bez ponownego przekierowania.
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/categories")).StatusCode);
        }

        // AB — zmiana roli unieważnia stary stan uwierzytelnienia (w obie strony).
        [Fact]
        public async Task RoleChange_InvalidatesStaleTokens_InBothDirections()
        {
            var admin = await AdminAsync();

            var promotedName = TestUsers.UniqueName("pr");
            var promoted = await _factory.CreateAuthenticatedApiClientAsync(promotedName);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/users/{IdOf(promotedName)}/role", new { role = "Admin" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await promoted.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await (await LoginAgainAsync(promotedName)).GetAsync("/api/moderation/reports")).StatusCode);

            var demotedName = TestUsers.UniqueName("de");
            var demoted = await _factory.CreateAuthenticatedApiClientAsync(demotedName, UserRole.Admin);
            Assert.Equal(HttpStatusCode.OK, (await demoted.GetAsync("/api/moderation/reports")).StatusCode);
            await admin.PutAsJsonAsync($"/api/users/{IdOf(demotedName)}/role", new { role = "User" });
            Assert.Equal(HttpStatusCode.Unauthorized, (await demoted.GetAsync("/api/moderation/reports")).StatusCode);

            var actions = _factory.WithScope(db => db.ModerationActions.Count(a => a.ActionType == SansPost.Features.Moderation.ModerationActionType.ChangeRole));
            Assert.True(actions >= 2);
        }

        [Fact]
        public async Task Admin_CannotModerateSelfOrAnotherAdmin()
        {
            var adminName = TestUsers.UniqueName("sa");
            var admin = await _factory.CreateAuthenticatedApiClientAsync(adminName, UserRole.Admin);
            var otherAdmin = TestUsers.UniqueName("oa");
            await _factory.CreateUserAsync(otherAdmin, UserRole.Admin);

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/moderation/users/{IdOf(adminName)}/ban", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/moderation/users/{IdOf(otherAdmin)}/ban", new { })).StatusCode);
        }
    }
}
