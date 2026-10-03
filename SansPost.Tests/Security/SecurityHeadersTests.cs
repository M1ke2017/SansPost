using System.Net;
using System.Text.RegularExpressions;
using SansPost.Infrastructure.Security;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    // Sprint 25: nagłówki bezpieczeństwa na stronach, API i błędach; CSP bez 'unsafe-eval', skrypty inline tylko z nonce
    // (tej samej odpowiedzi) albo z hashem statycznej strony błędu; bez inline onclick.
    [Trait("Category", "Security")]
    public class SecurityHeadersTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public SecurityHeadersTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

        [Fact]
        public async Task Page_HasSecurityHeaders_AndCspNonceMatchesInlineScripts()
        {
            var response = await _factory.CreateHttpsClient().GetAsync("/login");
            var html = await response.Content.ReadAsStringAsync();
            var csp = Header(response, "Content-Security-Policy");

            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
            Assert.Contains("camera=()", Header(response, "Permissions-Policy"));

            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("object-src 'none'", csp);
            Assert.DoesNotContain("unsafe-eval", csp);
            var scriptSrc = Regex.Match(csp, "script-src ([^;]*)").Groups[1].Value;
            Assert.DoesNotContain("unsafe-inline", scriptSrc);

            var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
            Assert.NotEmpty(nonce);
            var inline = Regex.Matches(html, "<script(?![^>]*\\bsrc=)([^>]*)>").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(inline);
            Assert.All(inline, attributes => Assert.Contains($"nonce=\"{nonce}\"", attributes));
            Assert.DoesNotMatch(new Regex("\\son[a-z]+=\"", RegexOptions.IgnoreCase), html);   // bez inline handlerów
        }

        // Regresja: nonce w base64 zawierał czasem "+", który Razor kodował w atrybucie (&#x2B;) — nonce w HTML przestawał
        // pasować do nagłówka i przeglądarka blokowała skrypty inline. Nonce: 32 znaki hex, identyczny w nagłówku i w HTML.
        [Fact]
        public async Task Nonce_IsFreshPerResponse_HexAndIdenticalInHtml()
        {
            var client = _factory.CreateHttpsClient();
            var nonces = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                var response = await client.GetAsync("/login");
                var nonce = Regex.Match(Header(response, "Content-Security-Policy"), "'nonce-([^']+)'").Groups[1].Value;
                Assert.Matches("^[0-9A-F]{32}$", nonce);
                Assert.Contains($"nonce=\"{nonce}\"", await response.Content.ReadAsStringAsync());
                nonces.Add(nonce);
            }
            Assert.Equal(nonces.Count, nonces.Distinct().Count());
        }

        [Fact]
        public async Task ApiAndErrors_AlsoCarryHeaders()
        {
            var client = _factory.CreateHttpsClient();
            foreach (var path in new[] { "/api/posts?limit=1", "/api/posts/999999", "/health/live" })
            {
                var response = await client.GetAsync(path);
                Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
                Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
            }
        }

        [Fact]
        public void ErrorPage_InlineScripts_AreAllowedByHash()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "SansPost.sln")))
                root = root.Parent;
            var errorPage = Path.Combine(root!.FullName, "SansPost", "wwwroot", "error.html");

            var hashes = SecurityHeaders.InlineScriptHashes(errorPage);
            var html = File.ReadAllText(errorPage);

            Assert.Equal(Regex.Matches(html, "<script>").Count, hashes.Count);
            Assert.True(hashes.Count >= 2);
            Assert.Contains(hashes[0], SecurityHeaders.Policy(new Microsoft.AspNetCore.Http.HostString("localhost"), "n", hashes));
            Assert.DoesNotMatch(new Regex("\\son[a-z]+=\"", RegexOptions.IgnoreCase), html);
        }
    }
}
