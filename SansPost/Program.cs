using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SansPost.Features.Comments;
using SansPost.Features.Demo;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Music;
using SansPost.Features.Notifications;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Features.Reactions;
using SansPost.Features.Search;
using SansPost.Hubs;
using SansPost.Infrastructure.Hosting;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;
using SansPost.Shared.Ui;

namespace SansPost;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Sonda HEALTHCHECK kontenera (obraz runtime nie ma curl/wget): "dotnet SansPost.dll --healthcheck".
        if (args.Contains("--healthcheck"))
            return await ContainerHealthProbe.RunAsync();

        var builder = WebApplication.CreateBuilder(args);

        // ---- 1. Konfiguracja ------------------------------------------------------------------------------------
        // Sekrety jako pliki (Docker secrets: /run/secrets/Jwt__Key → Jwt:Key). Nadpisują zmienne środowiskowe.
        builder.Configuration.AddKeyPerFile(builder.Configuration["SecretsDirectory"] ?? "/run/secrets", optional: true);
        builder.Services.AddSingleton(TimeProvider.System);

        // SIGTERM (docker stop): krótkie żądania kończą się, nowe nie są przyjmowane; bez ogromnego limitu.
        builder.Services.AddOptions<HostOptions>().Configure<IConfiguration>((options, configuration) =>
            options.ShutdownTimeout = configuration.GetValue<TimeSpan?>("Hosting:ShutdownTimeout") ?? TimeSpan.FromSeconds(15));

        // ---- 2. Baza danych ------------------------------------------------------------------------------------
        builder.Services.AddPersistence();

        // ---- 3. Bezpieczeństwo: cookie (Blazor) + JWT (REST), polityki, limity, reverse proxy, HTTPS, klucze ----
        builder.Services.AddSansPostSecurity(builder.Configuration);
        builder.Services.AddScoped<AuthStateValidator>();
        builder.Services.AddScoped<WriteGuard>();
        builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

        // ---- 4. Web: REST API, Blazor Server, health checks ----------------------------------------------------
        // requestId = X-Request-Id = RequestId w logach: zgłoszenie użytkownika da się powiązać z wpisem błędu.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);
        builder.Services.AddExceptionHandler<DatabaseFailureMetric>();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
        builder.Services.AddControllers()
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();
        builder.Services.AddHttpContextAccessor();   // prerender: status 404 dla nieznanych adresów (NotFoundStatus)
        builder.Services.AddScoped<ToastService>();
        builder.Services.AddScoped<SansPost.Shared.ScenePreference>();   // renderer przestrzeni (3D/CSS) w obrębie circuitu
        builder.Services.AddSingleton<UiServices>();   // osobny scope DI (DbContext) na każdą operację UI

        // live = proces działa (bez zależności), ready = PostgreSQL osiągalny.
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: new[] { DatabaseHealthCheck.ReadyTag });

        // ---- 5. Funkcje (wspólne dla REST i Blazor) ------------------------------------------------------------
        builder.Services.AddScoped<IAuthService, AuthService>();
        builder.Services.AddScoped<IAliasGenerator, AliasGenerator>();
        builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IPostService, PostService>();
        builder.Services.AddScoped<CommentService>();
        builder.Services.AddScoped<INotificationService, NotificationService>();
        builder.Services.AddScoped<LikeService>();
        builder.Services.AddScoped<ProfileService>();
        builder.Services.AddScoped<SearchService>();
        builder.Services.AddScoped<ReportService>();
        builder.Services.AddScoped<ModerationService>();

        // Start: bootstrap pierwszego admina, potem treści demo (post powitalny może mieć autora-admina).
        builder.Services.AddScoped<AdminBootstrapper>();
        builder.Services.AddHostedService<AdminBootstrapHostedService>();
        builder.Services.AddScoped<DemoContentSeeder>();
        builder.Services.AddHostedService<DemoContentHostedService>();
        builder.Services.AddScoped<FeaturedPostLocator>();

        // Kącik muzyczny: odkrywanie stacji country w Radio Browser (tylko lista — audio gra przeglądarka ze stacji),
        // pamięć podręczna procesu zamiast zapytań przy każdym wejściu. Krótkie limity czasu w RadioBrowserClient.
        builder.Services.AddOptions<MusicOptions>().BindConfiguration(MusicOptions.Section);
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(RadioBrowserClient.HttpClientName, (services, http) =>
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(services.GetRequiredService<IOptions<MusicOptions>>().Value.UserAgent);
            http.Timeout = Timeout.InfiniteTimeSpan;          // limity: RequestTimeout / TotalTimeout w kliencie
            http.MaxResponseContentBufferSize = 1024 * 1024;  // lista stacji, nie plik
        });
        builder.Services.AddSingleton<RadioBrowserClient>();
        builder.Services.AddSingleton<MusicService>();

        // Stół gry "Śladem Rewolwerowca": sesje pojedynków w pamięci procesu, rozstrzyganie w silniku F# (SansPost.Game.Core).
        // Sprint 20 — pojedynek na żywo: DuelHub (SignalR) nad tymi samymi serwisami; jedna instancja, bez backplane.
        builder.Services.AddOptions<DuelOptions>().BindConfiguration(DuelOptions.Section)
            .Validate(o => o.IsValid, "Duels: czasy wyzwania, rundy i powrotu muszą mieścić się w 1–300 s.").ValidateOnStart();
        builder.Services.AddSingleton<GameSessionService>();
        builder.Services.AddSingleton<DuelConnectionRegistry>();
        builder.Services.AddSingleton<DuelChallengeService>();
        builder.Services.AddSingleton<IDuelNotifier, HubDuelNotifier>();
        // Krótszy keep-alive niż domyślny (15 s / 30 s): zerwane połączenie bez zamknięcia (np. utrata sieci) serwer wykrywa
        // po ~12 s, więc okno powrotu zaczyna się szybko. Małe wiadomości — ruchy i akcje, bez treści użytkownika.
        builder.Services.AddSignalR().AddHubOptions<DuelHub>(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(12);
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.EnableDetailedErrors = false;
        });

        var app = builder.Build();

        // ---- 6. Start: pełna walidacja konfiguracji → baza i migracje → dopiero wtedy ruch ------------------------
        // Błąd = proces kończy się kodem 1 (komunikat bez wartości sekretów), nic nie jest obsługiwane "na pół".
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SansPost.Startup");
        try
        {
            app.Services.GetRequiredService<IStartupValidator>().Validate();
            await DatabaseStartup.MigrateDatabaseAsync(app.Services, startupLogger);
        }
        catch (Exception ex)
        {
            if (ex is OptionsValidationException invalid)
                startupLogger.LogCritical("Startup aborted: invalid configuration. {Failures}", string.Join(" | ", invalid.Failures));
            else
                startupLogger.LogCritical(ex, "Startup aborted: database is unavailable or migrations failed.");

            // Hostowane przez inny proces (WebApplicationFactory w testach): wyjątek wraca do hosta, który zwalnia host.
            if (System.Reflection.Assembly.GetEntryAssembly() != typeof(Program).Assembly)
                throw;
            await app.DisposeAsync();   // opróżnia logi przed wyjściem
            return 1;
        }

        // ---- 7. Middleware -------------------------------------------------------------------------------------
        // Za reverse proxy: prawdziwy adres klienta i schemat (https) zanim cokolwiek je odczyta (limity, cookie, HTTPS).
        app.UseForwardedHeaders();

        // Korelacja: X-Request-Id w każdej odpowiedzi (OnStarting — przetrwa też 500, bo handler wyjątków czyści nagłówki).
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
                return Task.CompletedTask;
            });
            return next(context);
        });

        if (!app.Environment.IsDevelopment())
        {
            // API: ogólny ProblemDetails 500. Strony: statyczna strona błędu. Nigdy komunikat wyjątku ani stack trace.
            app.UseExceptionHandler();
            app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), pages => pages.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "error.html"));
                }
            }));
            app.UseHsts();
        }

        // Puste odpowiedzi błędów API (401/403/404/429) jako ProblemDetails.
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api => api.UseStatusCodePages());

        // Sondy health (wewnętrzny HTTP kontenera) bez przekierowania na HTTPS.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), web => web.UseHttpsRedirection());
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseSessionEndedRedirect();
        app.UseAuthorization();

        // ---- 8. Endpointy --------------------------------------------------------------------------------------
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(DatabaseHealthCheck.ReadyTag) }).AllowAnonymous();
        app.MapControllers();
        app.MapBlazorHub();
        app.MapHub<DuelHub>(DuelHub.Path);
        app.MapFallbackToPage("/_Host");

        await app.RunAsync();
        return 0;
    }
}
