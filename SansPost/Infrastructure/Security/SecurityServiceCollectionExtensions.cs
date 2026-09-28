using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Hosting;

namespace SansPost.Infrastructure.Security
{
    public static class SecurityServiceCollectionExtensions
    {
        private const string CookieName = "SansPost.Auth";
        private const string SessionEndedItem = "SansPost.SessionEnded";
        private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

        public static IServiceCollection AddSansPostSecurity(this IServiceCollection services, IConfiguration configuration)
        {
            AddJwtOptions(services, configuration);
            AddPublicDemoOptions(services, configuration);
            services.AddSingleton<JwtTokenService>();

            // Cookie = domyślny schemat (Blazor UI). JWT tylko tam, gdzie wskazuje go polityka (REST API).
            services.AddAuthentication(AuthSchemes.Cookie)
                .AddCookie(AuthSchemes.Cookie)
                .AddJwtBearer(AuthSchemes.Jwt);

            services.AddOptions<CookieAuthenticationOptions>(AuthSchemes.Cookie)
                .Configure<IHostEnvironment>((options, environment) =>
                {
                    options.Cookie.Name = CookieName;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = environment.IsDevelopment()
                        ? CookieSecurePolicy.SameAsRequest
                        : CookieSecurePolicy.Always;
                    options.ExpireTimeSpan = CookieLifetime;
                    options.SlidingExpiration = true;
                    options.LoginPath = "/login";
                    options.AccessDeniedPath = "/login";

                    // Każde żądanie z cookie: aktualny stan konta (ban, zmiana roli/statusu → nowa AuthVersion).
                    // Niezgodność = principal odrzucony i cookie usunięte — nie czekamy na wygaśnięcie cookie.
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var validator = context.HttpContext.RequestServices.GetRequiredService<AuthStateValidator>();
                        if (context.Principal is null || !await validator.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(AuthSchemes.Cookie);
                            // UX: strona zamiast "cichego" trybu gościa pokaże komunikat o zakończonej sesji (UseSessionEndedRedirect).
                            context.HttpContext.Items[SessionEndedItem] = true;
                        }
                    };
                });

            services.AddOptions<JwtBearerOptions>(AuthSchemes.Jwt)
                .Configure<JwtTokenService>((options, tokens) =>
                {
                    options.TokenValidationParameters = tokens.ValidationParameters;

                    // Access token jest krótkotrwały, ale nie czekamy na wygaśnięcie: ban lub zmiana roli
                    // (nowa AuthVersion) odrzuca go przy następnym żądaniu. To security stamp, nie blacklista tokenów.
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async context =>
                        {
                            var validator = context.HttpContext.RequestServices.GetRequiredService<AuthStateValidator>();
                            if (context.Principal is null || !await validator.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                                context.Fail("Stan uwierzytelnienia jest nieaktualny.");
                        }
                    };
                });

            services.AddAuthorization(options =>
            {
                // [Authorize] bez polityki (strony Blazor) = zalogowany przez cookie.
                options.DefaultPolicy = new AuthorizationPolicyBuilder(AuthSchemes.Cookie)
                    .RequireAuthenticatedUser()
                    .Build();

                options.AddPolicy(AuthPolicies.ApiUser, policy => policy
                    .AddAuthenticationSchemes(AuthSchemes.Jwt)
                    .RequireAuthenticatedUser());

                options.AddPolicy(AuthPolicies.DuelPlayer, policy => policy
                    .AddAuthenticationSchemes(AuthSchemes.Cookie, AuthSchemes.Jwt)
                    .RequireAuthenticatedUser());

                options.AddPolicy(AuthPolicies.ApiAdmin, policy => policy
                    .AddAuthenticationSchemes(AuthSchemes.Jwt)
                    .RequireAuthenticatedUser()
                    .RequireRole(nameof(UserRole.Admin)));
            });

            AddRateLimiting(services, configuration);
            AddReverseProxyAndHttps(services, configuration);
            AddDataProtectionKeys(services);

            return services;
        }

        // Unieważnione cookie (ban, zawieszenie, przywrócenie, zmiana roli → nowa AuthVersion) przy wejściu na stronę:
        // zamiast wyświetlić ją po cichu jako gość — przekierowanie na logowanie z komunikatem "sesja zakończona".
        // Cookie jest już usunięte (SignOut w OnValidatePrincipal); REST (/api) i transport Blazora bez zmian.
        public static IApplicationBuilder UseSessionEndedRedirect(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (context.Items.ContainsKey(SessionEndedItem)
                && HttpMethods.IsGet(request.Method)
                && !request.Path.StartsWithSegments("/api")
                && !request.Path.StartsWithSegments("/_blazor")
                && !request.Path.StartsWithSegments("/login"))
            {
                var returnUrl = request.Path + request.QueryString;
                context.Response.Redirect("/login?ended=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
                return;
            }

            await next();
        });

        // Przydomki potrzebne w najgorszym przypadku: wszystkie sloty publiczne + konta demo (liczone ostrożnie jako rezerwa).
        public static int RequiredAliases(PublicDemoOptions options) =>
            options.MaxPublicAccounts + (options.SeedContent ? Features.Demo.DemoContent.Authors.Count : 0);

        // Reverse proxy: X-Forwarded-For/Proto tylko od jawnie zaufanych adresów (sekcja ForwardedHeaders).
        // HTTPS kończy się na proxy — przekierowanie tylko przy jawnie znanym porcie (HttpsRedirection:HttpsPort),
        // bez zgadywania i bez ostrzeżenia "Failed to determine the https port".
        private static void AddReverseProxyAndHttps(IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<ReverseProxyOptions>()
                .Bind(configuration.GetSection(ReverseProxyOptions.SectionName))
                .Validate(o => o.KnownProxies.All(ReverseProxyOptions.IsValidProxy), "ForwardedHeaders:KnownProxies zawiera nieprawidłowy adres IP.")
                .Validate(o => o.KnownNetworks.All(ReverseProxyOptions.IsValidNetwork), "ForwardedHeaders:KnownNetworks zawiera nieprawidłową sieć (oczekiwany CIDR, np. 10.0.0.0/24).")
                .Validate(o => o.ForwardLimit is >= 1 and <= 5, "ForwardedHeaders:ForwardLimit musi mieścić się w zakresie 1–5.")
                .ValidateOnStart();
            services.AddOptions<ForwardedHeadersOptions>()
                .Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) => proxy.Value.Apply(forwarded));

            services.AddHttpsRedirection(_ => { });
            services.AddOptions<HttpsRedirectionOptions>()
                .Configure<IConfiguration>((options, config) => options.HttpsPort = config.GetValue<int?>("HttpsRedirection:HttpsPort"));
        }

        // Klucze Data Protection (cookie, antiforgery, stan Blazora) na trwałym wolumenie (DataProtection:KeysPath) —
        // inaczej każde odtworzenie kontenera unieważnia sesje użytkowników.
        private static void AddDataProtectionKeys(IServiceCollection services)
        {
            services.AddDataProtection().SetApplicationName("SansPost");
            services.AddOptions<KeyManagementOptions>()
                .Configure<IConfiguration, ILoggerFactory>((options, configuration, loggers) =>
                {
                    if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
                        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keysPath), loggers);
                });
        }

        private static void AddJwtOptions(IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<JwtOptions>()
                .Bind(configuration.GetSection(JwtOptions.SectionName))
                .Validate(o => Encoding.UTF8.GetByteCount(o.Key) >= 32,
                    "Jwt:Key nie jest skonfigurowany lub jest krótszy niż 32 bajty. " +
                    "Development: dotnet user-secrets set \"Jwt:Key\" \"<losowy klucz>\". Deployment: zmienna środowiskowa Jwt__Key.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                    "Jwt:Issuer i Jwt:Audience muszą być skonfigurowane.")
                .Validate(o => o.AccessTokenLifetime >= TimeSpan.FromMinutes(1) && o.AccessTokenLifetime <= TimeSpan.FromMinutes(60),
                    "Jwt:AccessTokenLifetime musi mieścić się w zakresie 1–60 minut.")
                .Validate(o => o.RefreshTokenLifetime > o.AccessTokenLifetime && o.RefreshTokenLifetime <= TimeSpan.FromDays(90),
                    "Jwt:RefreshTokenLifetime musi być dłuższy od access tokena i nie dłuższy niż 90 dni.")
                .ValidateOnStart();
        }

        private static void AddPublicDemoOptions(IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<PublicDemoOptions>()
                .Bind(configuration.GetSection(PublicDemoOptions.SectionName))
                .Validate(o => o.MaxPublicAccounts > 0, "PublicDemo:MaxPublicAccounts musi być większe od 0.")
                .Validate(o => o.FeaturedPostId is null or > 0, "PublicDemo:FeaturedPostId musi być dodatnim identyfikatorem posta.")
                // Każde publiczne konto dostaje przydomek z generatora — limit kont (plus konta demo) musi zmieścić się
                // w bezpiecznej pojemności słownika, inaczej rejestracja zaczęłaby kończyć się brakiem wolnych przydomków.
                .Validate(o => RequiredAliases(o) <= WesternAliases.SafeCapacity,
                    $"PublicDemo:MaxPublicAccounts (wraz z kontami demo) przekracza bezpieczną pojemność generatora przydomków ({WesternAliases.SafeCapacity} z {WesternAliases.Capacity}).")
                .ValidateOnStart();

            // Bootstrap admina: przy Enabled = true dane muszą spełniać te same reguły co rejestracja.
            services.AddOptions<BootstrapAdminOptions>()
                .Bind(configuration.GetSection(BootstrapAdminOptions.SectionName))
                // Admin z konfiguracji operatora nie przechodzi przez publiczny generator przydomków (to nie jest konto publiczne).
                .Validate(o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.Username) && (RequestValidator.Validate(new RegisterRequest
                    {
                        Username = o.Username,
                        Email = o.Email,
                        Password = o.Password
                    }) ?? PasswordPolicy.Validate(o.Password)) is null),
                    "BootstrapAdmin jest włączony, ale Username/Email/Password są niepoprawne. " +
                    "Podaj je przez User Secrets lub zmienne środowiskowe (BootstrapAdmin__Username, ...).")
                .ValidateOnStart();
        }

        private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
        {
            foreach (var (name, defaultLimit) in new[] { (RateLimitPolicies.Auth, 10), (RateLimitPolicies.Search, 30), (RateLimitPolicies.Writes, 30) })
            {
                services.AddOptions<RateLimitWindowOptions>(name)
                    .Configure(o => o.PermitLimit = defaultLimit)
                    .Bind(configuration.GetSection($"RateLimiting:{name}"))
                    .Validate(o => o.PermitLimit > 0 && o.Window > TimeSpan.Zero, $"RateLimiting:{name} ma nieprawidłowe wartości.")
                    .ValidateOnStart();
            }

            // Zapisy zalogowanych — partycja UserId, wspólna dla REST i Blazor (egzekwowana w WriteGuard).
            services.AddSingleton(sp => new UserWriteRateLimiter(
                sp.GetRequiredService<IOptionsMonitor<RateLimitWindowOptions>>().Get(RateLimitPolicies.Writes)));

            // Wyszukiwanie — jeden limiter dla REST i Blazor (UI woła serwis bezpośrednio, z pominięciem middleware).
            services.AddSingleton(sp => new SearchRateLimiter(
                sp.GetRequiredService<IOptionsMonitor<RateLimitWindowOptions>>().Get(RateLimitPolicies.Search)));

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // 429 + Retry-After; treść ProblemDetails dopisuje UseStatusCodePages dla /api.
                options.OnRejected = (context, _) =>
                {
                    var endpoint = context.HttpContext.Request.Path.StartsWithSegments("/api/auth/alias-suggestion") ? "AliasSuggestion" : "Auth";
                    SansPost.Infrastructure.Hosting.SansPostTelemetry.RateLimitRejections.Add(1, new KeyValuePair<string, object?>("policy", endpoint));
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SansPost.RateLimiting")
                        .LogWarning("Rate limit {Policy} rejected {Method} {Path} from {ClientIp}.", endpoint, context.HttpContext.Request.Method,
                            context.HttpContext.Request.Path.Value, context.HttpContext.Connection.RemoteIpAddress?.ToString());

                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    // Formularze przeglądarki (/auth/login, /auth/register): zamiast pustej strony 429 — powrót do formularza
                    // z komunikatem. REST API (/api/*) zachowuje 429 + ProblemDetails.
                    var path = context.HttpContext.Request.Path;
                    if (path.StartsWithSegments("/auth/login") || path.StartsWithSegments("/auth/register"))
                    {
                        context.HttpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
                        context.HttpContext.Response.Headers.Location = path.StartsWithSegments("/auth/login")
                            ? "/login?error=rate-limited"
                            : "/register?error=rate-limited";
                    }

                    return ValueTask.CompletedTask;
                };

                // Anonimowe operacje — partycja per adres IP.
                // DEPLOYMENT (Sprint 11): za reverse proxy RemoteIpAddress to adres proxy — wymagane
                // ForwardedHeaders z listą zaufanych proxy. NIE ufamy bezwarunkowo X-Forwarded-For.
                options.AddPolicy(RateLimitPolicies.Auth, httpContext => PerIpWindow(httpContext, RateLimitPolicies.Auth));
                options.AddPolicy(RateLimitPolicies.Search, httpContext => PerIpWindow(httpContext, RateLimitPolicies.Search));
            });
        }

        private static RateLimitPartition<string> PerIpWindow(HttpContext httpContext, string policy)
        {
            var settings = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitWindowOptions>>().Get(policy);

            return RateLimitPartition.GetFixedWindowLimiter(
                $"{policy}:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = settings.Window,
                    QueueLimit = 0
                });
        }
    }
}
