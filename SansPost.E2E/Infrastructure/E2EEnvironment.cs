using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Npgsql;
using SansPost.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SansPost.E2E.Infrastructure
{
    // Środowisko E2E: PostgreSQL (Testcontainers) + opublikowany SansPost uruchomiony jako prawdziwy proces (Kestrel)
    // + Chromium (Playwright). Nic nie dotyka lokalnej bazy ani User Secrets — cała konfiguracja przez zmienne środowiskowe.
    public sealed class E2EEnvironment : IAsyncLifetime
    {
        public const string AdminUsername = "e2eadmin";
        public const string AdminEmail = "admin@e2e.test";
        public static readonly string AdminPassword = "Adm!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        public const string UserPassword = "correct-horse-battery";

        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        private readonly List<SansPostServer> _servers = new();
        private string? _publishDir;

        public IPlaywright Playwright { get; private set; } = null!;
        public IBrowser Browser { get; private set; } = null!;

        // Główna instancja: treści startowe, admin z bootstrapu, hojne limity.
        public SansPostServer Main { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await _postgres.StartAsync();
            _publishDir = await PublishAsync();

            Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

            Main = await StartServerAsync("main", new Dictionary<string, string?>
            {
                ["PublicDemo__SeedContent"] = "true",
                ["PublicDemo__MaxPublicAccounts"] = "1000"
            });
        }

        // Osobna instancja z własną, świeżą bazą (np. mały limit kont, ciasne limity zapytań, stabilny zrzut wizualny).
        public async Task<SansPostServer> StartServerAsync(string name, IDictionary<string, string?> settings)
        {
            var database = $"e2e_{name}_{Guid.NewGuid():N}"[..40];
            await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
                await create.ExecuteNonQueryAsync();
            }

            var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;
            await using (var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options))
                await context.Database.MigrateAsync();

            var environment = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "E2E",
                ["ConnectionStrings__DefaultConnection"] = connectionString,
                ["Jwt__Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                ["BootstrapAdmin__Enabled"] = "true",
                ["BootstrapAdmin__Username"] = AdminUsername,
                ["BootstrapAdmin__Email"] = AdminEmail,
                ["BootstrapAdmin__Password"] = AdminPassword,
                ["RateLimiting__Auth__PermitLimit"] = "10000",
                ["RateLimiting__Search__PermitLimit"] = "10000",
                ["RateLimiting__Writes__PermitLimit"] = "10000",
                ["Logging__LogLevel__Default"] = "Warning",
                ["Logging__LogLevel__SansPost"] = "Information"
            };
            foreach (var (key, value) in settings)
                environment[key] = value;

            var server = await SansPostServer.StartAsync(_publishDir!, environment, connectionString);
            _servers.Add(server);
            return server;
        }

        public async Task<IBrowserContext> NewContextAsync(SansPostServer? server = null, int width = 1280, int height = 800,
            ColorScheme colorScheme = ColorScheme.Light, bool reducedMotion = false)
        {
            var context = await Browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = (server ?? Main).BaseUrl,
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ColorScheme = colorScheme,
                ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference,
                Locale = "pl-PL"
            });
            context.SetDefaultTimeout(15_000);
            return context;
        }

        public async Task DisposeAsync()
        {
            foreach (var server in _servers)
                await server.DisposeAsync();
            if (Browser is not null)
                await Browser.DisposeAsync();
            Playwright?.Dispose();
            await _postgres.DisposeAsync();
        }

        // Jednorazowa publikacja (wwwroot + _framework jak na produkcji); uruchamiany jest ten sam artefakt co przy wdrożeniu.
        private static async Task<string> PublishAsync()
        {
            var repoRoot = FindRepoRoot();
            var output = Path.Combine(Path.GetTempPath(), "sanspost-e2e-publish");
            var info = new ProcessStartInfo("dotnet", $"publish \"{Path.Combine(repoRoot, "SansPost", "SansPost.csproj")}\" -c Debug -o \"{output}\" -nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"dotnet publish nie powiódł się:\n{await stdout}\n{await stderr}");
            return output;
        }

        public static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SansPost.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono SansPost.sln.");
        }
    }

    public sealed class SansPostServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new();

        private SansPostServer(Process process, string baseUrl, string connectionString)
        {
            _process = process;
            BaseUrl = baseUrl;
            ConnectionString = connectionString;
        }

        public string BaseUrl { get; }
        public string ConnectionString { get; }
        public string Log { get { lock (_log) return _log.ToString(); } }

        public static async Task<SansPostServer> StartAsync(string publishDir, IDictionary<string, string?> environment, string connectionString)
        {
            var port = FreePort();
            var baseUrl = $"http://localhost:{port}";
            var info = new ProcessStartInfo("dotnet", $"\"{Path.Combine(publishDir, "SansPost.dll")}\" --urls {baseUrl}")
            {
                WorkingDirectory = publishDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var (key, value) in environment)
                info.Environment[key] = value;

            var process = Process.Start(info)!;
            var server = new SansPostServer(process, baseUrl, connectionString);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (server._log) server._log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (server._log) server._log.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var http = new HttpClient();
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"SansPost zakończył działanie przy starcie:\n{server.Log}");
                try
                {
                    if ((await http.GetAsync(baseUrl + "/api/public/status")).StatusCode == HttpStatusCode.OK)
                        return server;
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(300);
            }

            throw new TimeoutException($"SansPost nie wystartował w 90 s:\n{server.Log}");
        }

        public HttpClient CreateApiClient() => new() { BaseAddress = new Uri(BaseUrl) };

        // Konto testowe przez REST (bez przydomka → przydział przez serwer).
        public async Task<TestUser> CreateUserAsync(string? alias = null)
        {
            var (email, username) = await RegisterUserAsync(alias);
            using var api = await CreateAuthenticatedApiClientAsync(email, E2EEnvironment.UserPassword);
            var me = await api.GetFromJsonAsync<RegisteredUser>("/api/auth/me");
            return new TestUser(me!.Id, username, email);
        }

        public async Task<(string Email, string Alias)> RegisterUserAsync(string? alias = null)
        {
            var email = $"e2e-{Guid.NewGuid():N}"[..20] + "@example.test";
            using var client = CreateApiClient();
            var response = await client.PostAsJsonAsync("/api/auth/register", new { username = alias, email, password = E2EEnvironment.UserPassword });
            if (response.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException($"Rejestracja nie powiodła się: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            var created = await response.Content.ReadFromJsonAsync<RegisteredUser>();
            return (email, created!.Username);
        }

        public async Task<HttpClient> CreateAuthenticatedApiClientAsync(string email, string password)
        {
            var client = CreateApiClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
            login.EnsureSuccessStatusCode();
            var token = await login.Content.ReadFromJsonAsync<TokenBody>();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token!.AccessToken);
            return client;
        }

        public Task<HttpClient> CreateAdminApiClientAsync() => CreateAuthenticatedApiClientAsync(E2EEnvironment.AdminEmail, E2EEnvironment.AdminPassword);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed record RegisteredUser(int Id, string Username);

        public sealed record TestUser(int Id, string Alias, string Email);

        private sealed record TokenBody(string AccessToken);
    }

    [CollectionDefinition(Name)]
    public sealed class E2ECollection : ICollectionFixture<E2EEnvironment>
    {
        public const string Name = "E2E";
    }
}
