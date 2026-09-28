using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Duels
{
    // REST /api/duels w pełnym pipeline (JWT, walidacja, ProblemDetails): dwóch graczy rozgrywa rundę przez HTTP;
    // tożsamość wyłącznie z tokena, ukryta karta przeciwnika nie wychodzi w odpowiedzi.
    [Trait("Category", "Duels")]
    public class DuelApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public DuelApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task TwoPlayers_CreateJoinMove_ServerResolvesRound()
        {
            var annaName = TestUsers.UniqueName("duel");
            var bartName = TestUsers.UniqueName("duel");
            using var anna = await _factory.CreateAuthenticatedApiClientAsync(annaName);
            using var bart = await _factory.CreateAuthenticatedApiClientAsync(bartName);

            var created = await anna.PostAsJsonAsync("/api/duels", new { mode = "challenge" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var duel = await created.ReadAsync<DuelSnapshot>();
            Assert.Equal("waiting-for-opponent", duel.Status);
            Assert.Equal(annaName, duel.You.Alias);
            Assert.EndsWith($"/api/duels/{duel.DuelId}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

            var joined = await (await bart.PostAsync($"/api/duels/{duel.DuelId}/join", null)).ReadAsync<DuelSnapshot>();
            Assert.Equal("in-progress", joined.Status);
            Assert.Equal(annaName, joined.Opponent!.Alias);

            var first = await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "shoot", round = 1 });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstBody = await first.Content.ReadAsStringAsync();
            Assert.Contains("\"resolved\":false", firstBody);

            var bartSees = await bart.GetStringAsync($"/api/duels/{duel.DuelId}");
            Assert.Contains("\"opponentReady\":true", bartSees);
            Assert.DoesNotContain("\"shoot\"", bartSees.Replace("\"card\":\"shoot\"", ""));   // karta Anny ukryta

            var second = await (await bart.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "dodge", round = 1 })).ReadAsync<MoveResponse>();
            Assert.True(second.Resolved);
            Assert.Equal(("dodge", "shoot"), (second.RoundResult!.YourCard, second.RoundResult.OpponentCard));
            Assert.Equal(0, (await (await anna.GetAsync($"/api/duels/{duel.DuelId}")).ReadAsync<DuelSnapshot>()).You.Ammo);

            var active = await (await bart.GetAsync("/api/duels/active")).ReadAsync<DuelSnapshot>();
            Assert.Equal(duel.DuelId, active.DuelId);
        }

        [Fact]
        public async Task Errors_AreProblemDetails_WithStableCodes()
        {
            using var anna = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("duel"));
            using var stranger = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("duel"));
            var duel = await (await anna.PostAsJsonAsync("/api/duels", new { mode = "training" })).ReadAsync<DuelSnapshot>();

            var noAuth = await _factory.CreateHttpsClient().GetAsync($"/api/duels/{duel.DuelId}");
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/duels/{duel.DuelId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "dodge", round = 1 })).StatusCode);

            var unknown = await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "lasso", round = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("duel-unknown-card", await unknown.Content.ReadAsStringAsync());

            var missing = await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { round = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

            var resolved = await (await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "dodge", round = 1 })).ReadAsync<MoveResponse>();
            Assert.True(resolved.Resolved);                                         // trening: manekin odpowiada od razu
            var stale = await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "dodge", round = 1 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("duel-stale-round", await stale.Content.ReadAsStringAsync());

            var badMode = await anna.PostAsJsonAsync("/api/duels", new { mode = "poker" });
            Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
        }
            // Sprint 19-FIX — zawieszone konto przez prawdziwą moderację: po ponownym logowaniu może czytać własny pojedynek,
        // ale nie tworzy, nie dołącza, nie gra i nie trenuje (403 account-suspended, jak przy zapisach treści). Stan bez zmian.
        [Fact]
        public async Task Suspended_CannotCreateJoinMoveOrTrain_ButReadsOwnDuel_StateUnchanged()
        {
            var annaName = TestUsers.UniqueName("duel");
            var bartName = TestUsers.UniqueName("duel");
            var hostName = TestUsers.UniqueName("duel");
            using var anna = await _factory.CreateAuthenticatedApiClientAsync(annaName);
            using var bart = await _factory.CreateAuthenticatedApiClientAsync(bartName);
            using var host = await _factory.CreateAuthenticatedApiClientAsync(hostName);
            using var admin = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("adm"), UserRole.Admin);

            var duel = await (await anna.PostAsJsonAsync("/api/duels", new { mode = "challenge" })).ReadAsync<DuelSnapshot>();
            await bart.PostAsync($"/api/duels/{duel.DuelId}/join", null);
            Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "taunt", round = 1 })).StatusCode);
            var waiting = await (await host.PostAsJsonAsync("/api/duels", new { mode = "challenge" })).ReadAsync<DuelSnapshot>();

            var bartId = _factory.WithScope(db => db.Users.Single(u => u.Username == bartName).Id);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/moderation/users/{bartId}/suspend", new { reason = "Spam" })).StatusCode);
            var tokens = await _factory.LoginApiAsync(_factory.CreateHttpsClient(), bartName);   // stara sesja unieważniona
            using var suspended = _factory.CreateHttpsClient();
            suspended.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

            var attempts = new[]
            {
                await suspended.PostAsJsonAsync($"/api/duels/{duel.DuelId}/moves", new { card = "dodge", round = 1 }),
                await suspended.PostAsJsonAsync("/api/duels", new { mode = "training" }),
                await suspended.PostAsJsonAsync("/api/duels", new { mode = "challenge" }),
                await suspended.PostAsync($"/api/duels/{waiting.DuelId}/join", null)
            };
            foreach (var response in attempts)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal("account-suspended", problem!.Extensions["code"]?.ToString());
            }

            // Odczyt własnego pojedynku: stan widoczny, ukryta karta przeciwnika nie.
            var read = await suspended.GetAsync($"/api/duels/{duel.DuelId}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var body = await read.Content.ReadAsStringAsync();
            Assert.Contains("\"opponentReady\":true", body);
            Assert.Contains("\"yourMove\":null", body);
            Assert.Contains("\"history\":[]", body);

            // Stan bez zmian: runda 1 nierozstrzygnięta, wolne miejsce w innym wyzwaniu nadal wolne.
            var annaView = await (await anna.GetAsync($"/api/duels/{duel.DuelId}")).ReadAsync<DuelSnapshot>();
            Assert.Equal((1, "taunt", false), (annaView.Round, annaView.YourMove, annaView.OpponentReady));
            Assert.Empty(annaView.History);
            Assert.Equal("waiting-for-opponent", (await (await host.GetAsync($"/api/duels/{waiting.DuelId}")).ReadAsync<DuelSnapshot>()).Status);
        }
    }
}
