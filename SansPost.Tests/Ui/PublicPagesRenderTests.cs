using System.Net;
using System.Net.Http.Json;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Ui
{
    // Prerender stron publicznych (gość) przez pełny pipeline: treść, a nie stany błędu; kodowanie treści użytkownika.
    // Współbieżne ładowanie komponentów (DbContext per operacja UI) weryfikuje UiPagesPostgresTests — na SQLite
    // in-memory zapytania kończą się synchronicznie i nie nakładają się.
    [Trait("Category", "Ui")]
    public class PublicPagesRenderTests : IClassFixture<SansPostFactory>
    {
        private const string FeedErrorText = "Nie udało się wczytać postów";

        private readonly SansPostFactory _factory;

        public PublicPagesRenderTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task GuestPages_RenderRealContent_NotErrorStates()
        {
            var author = TestUsers.UniqueName("ui");
            var api = await _factory.CreateAuthenticatedApiClientAsync(author);
            var title = $"Rozmowa o grach {Guid.NewGuid():N}"[..36];
            var created = await api.PostAsJsonAsync("/api/posts", new { title, content = "Treść <b>bez</b> HTML.", category = "Games" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var guest = _factory.CreateHttpsClient();
            foreach (var path in new[] { "/", "/posts", "/c/games", $"/u/{author}" })
            {
                var response = await guest.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var html = await response.Content.ReadAsStringAsync();
                Assert.Contains(title, html);
                Assert.DoesNotContain(FeedErrorText, html);
            }

            var categories = await guest.GetStringAsync("/categories");
            Assert.Contains("Gry", categories);
            Assert.Contains("Feedback", categories);
        }

        // Treść użytkownika to zwykły tekst: w HTML zawsze zakodowana, nigdy jako znaczniki.
        [Fact]
        public async Task PostDetail_RendersUserContentEncoded()
        {
            var api = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("ui"));
            var created = await api.PostAsJsonAsync("/api/posts", new { title = "Kodowanie treści", content = "<img src=x onerror=alert(1)>", category = "General" });
            var id = (await created.Content.ReadFromJsonAsync<IdOnly>())!.Id;

            var html = await _factory.CreateHttpsClient().GetStringAsync($"/post-view/{id}");

            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
            Assert.DoesNotContain("<img src=x", html);
        }

        [Fact]
        public async Task UnknownCategory_ShowsProductMessage()
        {
            var html = await _factory.CreateHttpsClient().GetStringAsync("/c/no-such-category");

            Assert.Contains("Nie ma takiej kategorii", html);
        }

        private sealed record IdOnly(int Id);
    }
}
