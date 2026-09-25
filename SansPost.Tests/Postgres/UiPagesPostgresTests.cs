using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Postgres
{
    // Strony Blazor (prerender) na prawdziwym PostgreSQL. Strona główna ładuje równolegle kilka komponentów
    // (kategorie w panelu bocznym + feed) — na SQLite in-memory operacje kończą się synchronicznie i problem jest
    // niewidoczny; tu zapytania realnie się nakładają. Współdzielony DbContext circuitu kończył się błędem feedu.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Ui")]
    public sealed class UiPagesPostgresTests : IDisposable
    {
        private readonly PostgresApiFactory _factory;

        public UiPagesPostgresTests(PostgresFixture pg)
        {
            _factory = new PostgresApiFactory(pg.ConnectionString);
        }

        public void Dispose() => _factory.Dispose();

        [DockerFact]
        public async Task HomeCategoryAndSearchPages_RenderResults_WithConcurrentComponentLoads()
        {
            var token = "ui" + Guid.NewGuid().ToString("N")[..10];
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("ui"));
            await author.CreatePostAsync($"Widoczny {token}");

            var guest = _factory.CreateHttpsClient();
            // Świeży post jest deterministycznie widoczny w "Najnowszych" i kategorii. W "Popularnych"
            // (wspólna baza kolekcji testów) jego pozycja zależy od innych testów — tam sprawdzamy tylko, że feed się wyrenderował.
            foreach (var (path, expectsNewPost) in new[] { ("/saloon", true), ("/c/general", true), ("/saloon?sort=popular", false) })
            {
                var html = await guest.GetStringAsync(path);

                if (expectsNewPost)
                    Assert.Contains(token, html);
                Assert.Contains("class=\"post-card", html);
                Assert.DoesNotContain("Nie udało się wczytać postów", html);
                Assert.DoesNotContain("Nie udało się wyświetlić tej strony", html); // ErrorBoundary layoutu
            }

            // Wyszukiwarka (Sprint 10): zapytanie wykonuje dopiero circuit — prerender pokazuje stan "Szukanie…", bez wyników
            // i bez zużycia limitu wyszukiwania (jedno wejście = jedno zapytanie). Wyniki w przeglądarce: E2E.
            var search = await guest.GetStringAsync($"/search?q={token}");
            Assert.Contains("Szukanie…", search);
            Assert.DoesNotContain("class=\"post-card", search);
            Assert.DoesNotContain("Nie udało się wyświetlić tej strony", search);
        }
    }
}
