using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SansPost.Features.Identity;

namespace SansPost.Infrastructure.Security
{
    public static class SecurityServiceCollectionExtensions
    {
        private const string CookieName = "SansPost.Auth";
        private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

        public static IServiceCollection AddSansPostSecurity(this IServiceCollection services, IConfiguration configuration)
        {
            AddJwtOptions(services, configuration);
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
                });

            services.AddOptions<JwtBearerOptions>(AuthSchemes.Jwt)
                .Configure<JwtTokenService>((options, tokens) => options.TokenValidationParameters = tokens.ValidationParameters);

            services.AddAuthorization(options =>
            {
                // [Authorize] bez polityki (strony Blazor) = zalogowany przez cookie.
                options.DefaultPolicy = new AuthorizationPolicyBuilder(AuthSchemes.Cookie)
                    .RequireAuthenticatedUser()
                    .Build();

                options.AddPolicy(AuthPolicies.ApiUser, policy => policy
                    .AddAuthenticationSchemes(AuthSchemes.Jwt)
                    .RequireAuthenticatedUser());

                options.AddPolicy(AuthPolicies.ApiAdmin, policy => policy
                    .AddAuthenticationSchemes(AuthSchemes.Jwt)
                    .RequireAuthenticatedUser()
                    .RequireRole(nameof(UserRole.Admin)));
            });

            AddAuthRateLimiting(services, configuration);

            return services;
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

        private static void AddAuthRateLimiting(IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<AuthRateLimitOptions>()
                .Bind(configuration.GetSection(AuthRateLimitOptions.SectionName))
                .Validate(o => o.PermitLimit > 0 && o.Window > TimeSpan.Zero, "RateLimiting:Auth ma nieprawidłowe wartości.")
                .ValidateOnStart();

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // Partycja per adres IP. Za reverse proxy wymaga ForwardedHeaders (deployment).
                options.AddPolicy(AuthRateLimitOptions.PolicyName, httpContext =>
                {
                    var settings = httpContext.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;

                    return RateLimitPartition.GetFixedWindowLimiter(
                        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = settings.PermitLimit,
                            Window = settings.Window,
                            QueueLimit = 0
                        });
                });
            });
        }
    }
}
