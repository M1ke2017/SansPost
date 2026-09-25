using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.Postgres;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Deployment
{
    // Sprint 11 — konfiguracja produkcyjna, fail-fast, reverse proxy, health checks, pula połączeń.
    [Trait("Category", "Deployment")]
    public class ProductionConfigurationTests
    {
        private sealed class ProductionFactory : SansPostFactory
        {
            private readonly IDictionary<string, string?> _overrides;

            public ProductionFactory(IDictionary<string, string?> overrides) => _overrides = overrides;

            protected override string EnvironmentName => "Production";

            protected override IDictionary<string, string?> Settings
            {
                get
                {
                    var settings = base.Settings;
                    foreach (var (key, value) in _overrides)
                        settings[key] = value;
                    return settings;
                }
            }
        }

        // 1, 2, 11 — Production bez klucza JWT / bez bazy / ze złą pulą: start przerwany z czytelnym komunikatem,
        // bez wartości sekretów w komunikacie.
        [Theory]
        [InlineData("Jwt:Key", "", "Jwt:Key")]
        [InlineData("ConnectionStrings:DefaultConnection", "", "DefaultConnection")]
        [InlineData("Database:MaxPoolSize", "0", "Database:MaxPoolSize")]
        [InlineData("Database:MaxPoolSize", "5000", "Database:MaxPoolSize")]
        [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip", "ForwardedHeaders:KnownProxies")]
        public void Production_MissingOrInvalidCriticalConfiguration_FailsFast(string key, string value, string expectedInMessage)
        {
            using var factory = new ProductionFactory(new Dictionary<string, string?> { [key] = value });

            var exception = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(exception);
            Assert.Contains(expectedInMessage, exception!.ToString());
            Assert.DoesNotContain(SansPostFactory.JwtKey, exception.ToString());
        }

        // 3, 4 — pliki konfiguracji dostarczane z aplikacją: bezpieczne domyślne wartości produkcyjne, bez sekretów.
        [Fact]
        public void ShippedProductionConfiguration_HasSafeDefaults_AndNoSecrets()
        {
            var contentRoot = Path.Combine(E2ERoot(), "SansPost");
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(contentRoot, "appsettings.json"))
                .AddJsonFile(Path.Combine(contentRoot, "appsettings.Production.json"))
                .Build();

            var demo = configuration.GetSection(PublicDemoOptions.SectionName).Get<PublicDemoOptions>()!;
            var bootstrap = configuration.GetSection(BootstrapAdminOptions.SectionName).Get<BootstrapAdminOptions>() ?? new BootstrapAdminOptions();

            Assert.False(demo.SeedContent);
            Assert.False(bootstrap.Enabled);
            Assert.False(new BootstrapAdminOptions().Enabled);
            Assert.NotNull(configuration["PublicDemo:RegistrationEnabled"]);   // jawnie ustawione
            Assert.NotNull(configuration["PublicDemo:MaxPublicAccounts"]);
            Assert.Equal("30", configuration["Database:MaxPoolSize"]);

            Assert.True(string.IsNullOrEmpty(configuration["Jwt:Key"]));
            Assert.True(string.IsNullOrEmpty(configuration.GetConnectionString("DefaultConnection")));
            Assert.True(string.IsNullOrEmpty(configuration["BootstrapAdmin:Password"]));
            Assert.Equal("json", configuration["Logging:Console:FormatterName"]);
        }

        // 11 — pula połączeń: domyślnie 30, hasło może przyjść osobno (sekret), nazwa aplikacji w pg_stat_activity.
        [Fact]
        public void ConnectionString_IsBuiltWithBoundedPool_AndSeparatePassword()
        {
            var options = new DatabaseOptions { ConnectionString = "Host=db;Database=sanspost;Username=app", Password = "from-secret-file\n" };

            var built = new Npgsql.NpgsqlConnectionStringBuilder(options.BuildConnectionString());

            Assert.Equal(DatabaseOptions.DefaultMaxPoolSize, built.MaxPoolSize);
            Assert.Equal(30, built.MaxPoolSize);
            Assert.Equal("from-secret-file", built.Password);
            Assert.Equal("SansPost", built.ApplicationName);
            Assert.Equal("db:5432/sanspost", options.DescribeTarget());
            Assert.DoesNotContain("from-secret-file", options.DescribeTarget());
        }

        private static string E2ERoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SansPost.sln")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    // 5, 6, 7 — ForwardedHeaders za zaufanym proxy. TestServer nie ma adresu klienta, więc filtr startowy ustawia
    // RemoteIpAddress z nagłówka testowego PRZED potokiem aplikacji (tak jak zrobiłby to Kestrel dla połączenia TCP).
    [Trait("Category", "Deployment")]
    public class ForwardedHeadersTests : IClassFixture<ForwardedHeadersTests.ProxyFactory>
    {
        public const string TrustedProxy = "10.0.0.10";
        private const string RemoteIpHeader = "X-Test-Remote-Ip";

        public sealed class ProxyFactory : SansPostFactory
        {
            protected override IDictionary<string, string?> Settings
            {
                get
                {
                    var settings = base.Settings;
                    settings["ForwardedHeaders:KnownProxies:0"] = TrustedProxy;
                    settings["HttpsRedirection:HttpsPort"] = "443";
                    settings["RateLimiting:Search:PermitLimit"] = "2";
                    return settings;
                }
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteIpStartupFilter>());
            }
        }

        private sealed class RemoteIpStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip))
                        context.Connection.RemoteIpAddress = IPAddress.Parse(ip!);
                    return nextMiddleware(context);
                });
                next(app);
            };
        }

        private readonly ProxyFactory _factory;

        public ForwardedHeadersTests(ProxyFactory factory)
        {
            _factory = factory;
        }

        private HttpClient HttpClient() => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        private static HttpRequestMessage Request(string path, string remoteIp, string? forwardedFor = null, string? forwardedProto = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(RemoteIpHeader, remoteIp);
            if (forwardedFor is not null)
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            if (forwardedProto is not null)
                request.Headers.Add("X-Forwarded-Proto", forwardedProto);
            return request;
        }

        // 5 — TLS kończy się na proxy: X-Forwarded-Proto=https od zaufanego proxy → Request.Scheme=https (brak
        // przekierowania, brak pętli). Ten sam nagłówek od nieznanego nadawcy jest ignorowany → zwykłe przekierowanie.
        [Fact]
        public async Task ForwardedProtoHttps_FromTrustedProxy_IsHttps_NoRedirectLoop()
        {
            var http = HttpClient();

            var viaProxy = await http.SendAsync(Request("/categories", TrustedProxy, "198.51.100.7", "https"));
            Assert.Equal(HttpStatusCode.OK, viaProxy.StatusCode);

            var plainHttp = await http.SendAsync(Request("/categories", TrustedProxy));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, plainHttp.StatusCode);
            Assert.Equal("https://localhost/categories", plainHttp.Headers.Location!.ToString());

            var spoofedProto = await http.SendAsync(Request("/categories", "192.0.2.50", "198.51.100.7", "https"));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, spoofedProto.StatusCode);

            // Sondy health (wewnętrzny HTTP kontenera) nie są przekierowywane.
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Request("/health/live", "192.0.2.50"))).StatusCode);
        }

        // 6 — zaufane proxy: limiter widzi prawdziwy adres klienta (X-Forwarded-For), więc klienci za tym samym proxy
        // mają osobne limity.
        [Fact]
        public async Task ForwardedFor_FromTrustedProxy_IsTheClientIdentityForRateLimiting()
        {
            var http = HttpClient();
            async Task<HttpStatusCode> Search(string client) =>
                (await http.SendAsync(Request("/api/search/users?q=ab", TrustedProxy, client, "https"))).StatusCode;

            Assert.Equal(HttpStatusCode.OK, await Search("203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, await Search("203.0.113.1"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await Search("203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, await Search("203.0.113.2"));   // inny klient za tym samym proxy
        }

        // 7 — nadawca spoza listy zaufanych: X-Forwarded-For/Proto ignorowane — limiter liczy na adresie połączenia,
        // więc podmiana nagłówka nie daje nowego limitu, a "https" w nagłówku nie wyłącza przekierowania.
        [Fact]
        public async Task ForwardedFor_FromUntrustedSender_IsIgnored_CannotSpoofClientIp()
        {
            var direct = await HttpClient().SendAsync(Request("/api/search/users?q=ab", "192.0.2.77", "203.0.113.9", "https"));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, direct.StatusCode);

            // To samo połączenie po HTTPS (bez przekierowania): różne X-Forwarded-For, jeden limit.
            var https = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });
            async Task<HttpStatusCode> Search(string spoofed) =>
                (await https.SendAsync(Request("/api/search/users?q=ab", "192.0.2.77", spoofed))).StatusCode;

            Assert.Equal(HttpStatusCode.OK, await Search("203.0.113.21"));
            Assert.Equal(HttpStatusCode.OK, await Search("203.0.113.22"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await Search("203.0.113.23"));   // podmiana XFF nic nie daje
        }
    }

    // 8, 10 — health: live nie zależy od bazy; ready = 503 bez szczegółów, gdy PostgreSQL jest niedostępny.
    // Prawdziwa rejestracja Npgsql z konfiguracji (bez podmiany na SQLite), baza na zamkniętym porcie.
    [Trait("Category", "Deployment")]
    public class HealthWithoutDatabaseTests : IClassFixture<HealthWithoutDatabaseTests.DeadDatabaseFactory>
    {
        public sealed class DeadDatabaseFactory : SansPostFactory
        {
            protected override bool ReplaceDatabase => false;

            protected override IDictionary<string, string?> Settings
            {
                get
                {
                    var settings = base.Settings;
                    settings["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=sanspost;Username=sanspost;Timeout=2";
                    settings["Database:Password"] = "secret-db-password-xyz";
                    return settings;
                }
            }

            protected override void InitializeDatabase(ApplicationDbContext context)
            {
            }
        }

        private readonly DeadDatabaseFactory _factory;

        public HealthWithoutDatabaseTests(DeadDatabaseFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Live_IsHealthy_Ready_IsUnhealthyWithoutDetails()
        {
            var http = _factory.CreateHttpsClient();

            var live = await http.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());

            var ready = await http.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            var body = await ready.Content.ReadAsStringAsync();
            Assert.Equal("Unhealthy", body);
            Assert.DoesNotContain("secret-db-password-xyz", body);

            // Błąd bazy w API: ogólny 500 z requestId, bez SQL/Npgsql/connection stringu.
            var api = await http.GetAsync("/api/posts");
            Assert.Equal(HttpStatusCode.InternalServerError, api.StatusCode);
            var problem = await api.Content.ReadAsStringAsync();
            Assert.Contains("requestId", problem);
            Assert.DoesNotMatch("(?i)npgsql|exception|host=|password|127\\.0\\.0\\.1", problem);
            Assert.True(api.Headers.Contains("X-Request-Id"));
        }

        // Pula z konfiguracji trafia do faktycznego connection stringu EF/Npgsql.
        [Fact]
        public void DbContext_UsesBoundedPoolFromConfiguration()
        {
            using var scope = _factory.Services.CreateScope();
            var connectionString = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!;

            Assert.Equal(30, new Npgsql.NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize);
            Assert.Equal(30, scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
        }
    }

    // 9 + strategia migracji na prawdziwym PostgreSQL.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Deployment")]
    public class DeploymentPostgresTests
    {
        private readonly PostgresFixture _pg;

        public DeploymentPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        [DockerFact]
        public async Task Ready_IsHealthy_WithPostgreSql()
        {
            using var factory = new PostgresApiFactory(_pg.ConnectionString);
            var ready = await factory.CreateHttpsClient().GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        }

        // Pusta baza → start aplikacji stosuje wszystkie migracje; kolejny start niczego nie zmienia (idempotentny).
        [DockerFact]
        public async Task StartupMigrations_ApplyAllOnEmptyDatabase_AndAreIdempotent()
        {
            var connectionString = await _pg.CreateDatabaseAsync($"deploy_{Guid.NewGuid():N}"[..30]);
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString
                }).Build())
                .AddPersistence()
                .BuildServiceProvider();
            var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("test");

            await SansPost.Infrastructure.Hosting.DatabaseStartup.MigrateDatabaseAsync(services, logger);
            await using (var context = _pg.CreateContext(connectionString))
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.Equal(context.Database.GetMigrations().Count(), (await context.Database.GetAppliedMigrationsAsync()).Count());
            }

            await SansPost.Infrastructure.Hosting.DatabaseStartup.MigrateDatabaseAsync(services, logger);   // drugi start: no-op
        }
    }
}

namespace SansPost.Tests.Deployment
{
    // Metryki (System.Diagnostics.Metrics): odrzucenie przez limit jest liczone z tagiem polityki.
    [Trait("Category", "Deployment")]
    public class MetricsTests
    {
        [Fact]
        public async Task RateLimitRejection_IsCounted_WithPolicyTag()
        {
            var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SansPost.Infrastructure.Hosting.SansPostTelemetry.MeterName && instrument.Name == "sanspost.rate_limit.rejections")
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "policy")
                        observed.Add(tag.Value?.ToString() ?? "");
            });
            listener.Start();

            using var factory = new SansPost.Tests.Moderation.TightLimitsFactory();
            var client = factory.CreateHttpsClient();
            for (var i = 0; i < 4; i++)
                await client.GetAsync("/api/search/users?q=ab");

            Assert.Contains("Search", observed);
        }
    }
}
