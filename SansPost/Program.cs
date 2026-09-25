using System.Data.Common;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SansPost.Features.Comments;
using Microsoft.AspNetCore.Components.Authorization;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Features.Reactions;
using SansPost.Features.Search;
using SansPost.Infrastructure.Hosting;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Sonda HEALTHCHECK kontenera (obraz runtime nie ma curl/wget): "dotnet SansPost.dll --healthcheck".
        if (args.Contains("--healthcheck"))
            return await ContainerHealthProbe.RunAsync();

        var builder = WebApplication.CreateBuilder(args);

        // Sekrety jako pliki (Docker secrets: /run/secrets/Jwt__Key → Jwt:Key). Nadpisują zmienne środowiskowe.
        builder.Configuration.AddKeyPerFile(builder.Configuration["SecretsDirectory"] ?? "/run/secrets", optional: true);

        builder.Services.AddSingleton(TimeProvider.System);

        // Persistence + Security (Cookie dla Blazor, JWT + refresh token dla REST)
        builder.Services.AddPersistence();
        builder.Services.AddSansPostSecurity(builder.Configuration);

        // Reverse proxy: X-Forwarded-For/Proto tylko od jawnie zaufanych adresów (sekcja ForwardedHeaders).
        builder.Services.AddOptions<ReverseProxyOptions>()
            .Bind(builder.Configuration.GetSection(ReverseProxyOptions.SectionName))
            .Validate(o => o.KnownProxies.All(ReverseProxyOptions.IsValidProxy), "ForwardedHeaders:KnownProxies zawiera nieprawidłowy adres IP.")
            .Validate(o => o.KnownNetworks.All(ReverseProxyOptions.IsValidNetwork), "ForwardedHeaders:KnownNetworks zawiera nieprawidłową sieć (oczekiwany CIDR, np. 10.0.0.0/24).")
            .Validate(o => o.ForwardLimit is >= 1 and <= 5, "ForwardedHeaders:ForwardLimit musi mieścić się w zakresie 1–5.")
            .ValidateOnStart();
        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) => proxy.Value.Apply(forwarded));

        // HTTPS kończy się na reverse proxy. Przekierowanie tylko przy jawnie znanym porcie HTTPS
        // (HttpsRedirection:HttpsPort) — bez zgadywania i bez ostrzeżenia "Failed to determine the https port".
        builder.Services.AddHttpsRedirection(_ => { });
        builder.Services.AddOptions<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>()
            .Configure<IConfiguration>((options, configuration) => options.HttpsPort = configuration.GetValue<int?>("HttpsRedirection:HttpsPort"));

        // Klucze Data Protection (cookie, antiforgery, stan Blazora) na trwałym wolumenie — inaczej każde odtworzenie
        // kontenera unieważnia sesje użytkowników.
        builder.Services.AddDataProtection().SetApplicationName("SansPost");
        builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<IConfiguration, ILoggerFactory>((options, configuration, loggers) =>
            {
                if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
                    options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(keysPath), loggers);
            });

        // SIGTERM (docker stop): krótkie żądania kończą się, nowe nie są przyjmowane; bez ogromnego limitu.
        builder.Services.AddOptions<HostOptions>().Configure<IConfiguration>((options, configuration) =>
            options.ShutdownTimeout = configuration.GetValue<TimeSpan?>("Hosting:ShutdownTimeout") ?? TimeSpan.FromSeconds(15));

        // Health: live = proces działa (bez zależności), ready = PostgreSQL osiągalny.
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: new[] { DatabaseHealthCheck.ReadyTag });
        builder.Services.AddExceptionHandler<DatabaseFailureMetric>();

        // HTTP API (Controllers) + Blazor Server
        // requestId = X-Request-Id = RequestId w logach: zgłoszenie użytkownika da się powiązać z wpisem błędu.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
        builder.Services.AddControllers()
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();
        builder.Services.AddHttpContextAccessor();   // prerender: status 404 dla nieznanych adresów (NotFoundStatus)

        // Features — wspólne dla Controllers i Blazor UI
        builder.Services.AddScoped<IAuthService, AuthService>();
        builder.Services.AddScoped<IAliasGenerator, AliasGenerator>();
        builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IPostService, PostService>();
        builder.Services.AddScoped<ICommentService, CommentService>();
        builder.Services.AddScoped<SansPost.Features.Notifications.INotificationService, SansPost.Features.Notifications.NotificationService>();
        builder.Services.AddScoped<ILikeService, LikeService>();
        builder.Services.AddScoped<IProfileService, ProfileService>();
        builder.Services.AddScoped<ISearchService, PostgresSearchService>();
        builder.Services.AddScoped<IReportService, ReportService>();
        builder.Services.AddScoped<IModerationService, ModerationService>();

        // Stan konta: walidacja sesji (AuthVersion), brama zapisów, bootstrap pierwszego admina.
        builder.Services.AddScoped<IAuthStateValidator, AuthStateValidator>();
        builder.Services.AddScoped<IWriteGuard, WriteGuard>();
        builder.Services.AddScoped<AdminBootstrapper>();
        builder.Services.AddHostedService<AdminBootstrapHostedService>();

        // Publiczne demo: treści startowe (po bootstrapie admina — post powitalny może mieć autora-admina) i post przypięty.
        builder.Services.AddScoped<SansPost.Features.Demo.DemoContentSeeder>();
        builder.Services.AddHostedService<SansPost.Features.Demo.DemoContentHostedService>();
        builder.Services.AddScoped<SansPost.Features.Demo.IFeaturedPostLocator, SansPost.Features.Demo.FeaturedPostLocator>();

        // UI: powiadomienia (toast) w obrębie jednego circuitu; osobny scope DI na każdą operację UI (DbContext per operacja).
        builder.Services.AddScoped<SansPost.Shared.Ui.ToastService>();
        builder.Services.AddSingleton<SansPost.Shared.Ui.UiServices>();

        // Blazor: circuit okresowo sprawdza aktualność sesji (ban/zmiana roli wylogowuje także otwartą kartę).
        builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

        var app = builder.Build();

        // Start: najpierw pełna walidacja konfiguracji (czytelny komunikat, bez wartości sekretów), potem baza
        // i migracje — dopiero wtedy ruch. Błąd = proces kończy się kodem ≠ 0, nic nie jest obsługiwane "na pół".
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SansPost.Startup");
        try
        {
            app.Services.GetRequiredService<IStartupValidator>().Validate();
            await DatabaseStartup.MigrateDatabaseAsync(app.Services, startupLogger);
        }
        catch (OptionsValidationException ex)
        {
            startupLogger.LogCritical("Startup aborted: invalid configuration. {Failures}", string.Join(" | ", ex.Failures));
            if (HostedByAnotherProcess)
                throw;   // host należy do procesu hostującego (testy) — on go zwalnia
            await app.DisposeAsync();   // opróżnia logi przed wyjściem
            return 1;
        }
        catch (Exception ex)
        {
            startupLogger.LogCritical(ex, "Startup aborted: database is unavailable or migrations failed.");
            if (HostedByAnotherProcess)
                throw;   // host należy do procesu hostującego (testy) — on go zwalnia
            await app.DisposeAsync();   // opróżnia logi przed wyjściem
            return 1;
        }

        // Za reverse proxy: prawdziwy adres klienta i schemat (https) zanim cokolwiek je odczyta (limity, cookie, HTTPS).
        app.UseForwardedHeaders();

        // Korelacja: identyfikator żądania w odpowiedzi = RequestId w logach (scope) = traceId w ProblemDetails.
        // OnStarting: nagłówek przetrwa także odpowiedź 500 (handler wyjątków czyści nagłówki).
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
            // Wyjątki jako ogólny ProblemDetails 500 — bez komunikatu i stack trace.
            app.UseExceptionHandler();

            // Strony (nie /api): statyczna strona błędu HTML zamiast ProblemDetails JSON — bez szczegółów wyjątku.
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

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(DatabaseHealthCheck.ReadyTag) }).AllowAnonymous();
        app.MapControllers();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");

        await app.RunAsync();
        return 0;
    }

    // Błąd startu: w procesie aplikacji czysty kod wyjścia 1 (orkiestrator widzi błąd, log już zapisany). Gdy Main jest
    // hostowany przez inny proces (WebApplicationFactory w testach), wyjątek wraca do hosta — test sprawdza komunikat.
    private static bool HostedByAnotherProcess => System.Reflection.Assembly.GetEntryAssembly() != typeof(Program).Assembly;
}

// Nieobsłużony błąd bazy w żądaniu → metryka (obsługę i odpowiedź 500 zostawia standardowy handler).
internal sealed class DatabaseFailureMetric : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is DbException || exception.GetBaseException() is DbException)
            SansPostTelemetry.DatabaseFailures.Add(1, new KeyValuePair<string, object?>("source", "request"));
        return ValueTask.FromResult(false);
    }
}
