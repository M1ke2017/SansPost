using System.Text.RegularExpressions;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Ui
{
    // Sprint 23: pliki z wersją w adresie (asp-append-version) — cache na rok bez rewalidacji; moduły bez wersji
    // (sceny 3D, Three.js) — zawsze rewalidacja ETag, żeby po wdrożeniu przeglądarka nie użyła starej wersji.
    [Trait("Category", "Ui")]
    public class StaticAssetCachingTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public StaticAssetCachingTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task VersionedAssets_AreImmutable_UnversionedModules_Revalidate()
        {
            var client = _factory.CreateHttpsClient();
            var html = await client.GetStringAsync("/login");
            var css = Regex.Match(html, @"css/app\.css\?v=[\w-]+").Value;
            Assert.NotEmpty(css);

            var versioned = await client.GetAsync("/" + css);
            Assert.True(versioned.IsSuccessStatusCode);
            Assert.Equal("public, max-age=31536000, immutable", versioned.Headers.CacheControl?.ToString());

            foreach (var path in new[] { "/js/saloon3d.js", "/js/scene3d-common.js", "/lib/three/three.module.min.js", "/css/app.css" })
            {
                var response = await client.GetAsync(path);
                Assert.True(response.IsSuccessStatusCode, path);
                Assert.True(response.Headers.CacheControl?.NoCache, path);
                Assert.NotNull(response.Headers.ETag);

                // Rewalidacja: ten sam ETag → 304 bez treści.
                var revalidate = new HttpRequestMessage(HttpMethod.Get, path);
                revalidate.Headers.IfNoneMatch.Add(response.Headers.ETag!);
                Assert.Equal(System.Net.HttpStatusCode.NotModified, (await client.SendAsync(revalidate)).StatusCode);
            }
        }
    }
}
