using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Tests.TestInfrastructure
{
    // Pełny pipeline aplikacji (middleware, auth, polityki, rate limiting) na SQLite in-memory.
    public class SansPostFactory : WebApplicationFactory<Program>
    {
        public const string JwtKey = "integration-tests-signing-key-0123456789abcdef";
        public const string Issuer = "SansPost";
        public const string Audience = "SansPostUser";

        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=not-used-in-tests",
            ["Jwt:Key"] = JwtKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["RateLimiting:Auth:PermitLimit"] = "1000"
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            // "Testing": bez User Secrets, cookie Secure=Always, produkcyjny exception handler.
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

            return host;
        }

        public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        public async Task<int> CreateUserAsync(string username, UserRole role = UserRole.User, string password = TestUsers.Password)
        {
            using var scope = Services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

            var result = await auth.RegisterAsync(new RegisterRequest
            {
                Username = username,
                Email = $"{username}@example.com",
                Password = password
            });
            Assert.True(result.Succeeded, result.Message);

            if (role != UserRole.User)
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var user = await db.Users.SingleAsync(u => u.Id == result.Value!.Id);
                user.Role = role;
                await db.SaveChangesAsync();
            }

            return result.Value!.Id;
        }

        public async Task<TokenResponse> LoginApiAsync(HttpClient client, string username, string password = TestUsers.Password)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { email = $"{username}@example.com", password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        }

        public async Task<HttpClient> CreateAuthenticatedApiClientAsync(string username, UserRole role = UserRole.User)
        {
            await CreateUserAsync(username, role);
            var client = CreateHttpsClient();
            var tokens = await LoginApiAsync(client, username);
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
            return client;
        }

        public T WithScope<T>(Func<ApplicationDbContext, T> query)
        {
            using var scope = Services.CreateScope();
            return query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public static string ExtractAntiforgeryToken(string html)
        {
            var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.True(match.Success, "Brak tokenu antiforgery w odpowiedzi HTML.");
            return match.Groups[1].Value;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _connection.Dispose();
        }
    }
}
