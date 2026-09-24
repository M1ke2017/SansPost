using System.Text.Json.Serialization;
using SansPost.Features.Comments;
using Microsoft.AspNetCore.Components.Authorization;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Features.Reactions;
using SansPost.Features.Search;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton(TimeProvider.System);

        // Persistence + Security (Cookie dla Blazor, JWT + refresh token dla REST)
        builder.Services.AddPersistence();
        builder.Services.AddSansPostSecurity(builder.Configuration);

        // HTTP API (Controllers) + Blazor Server
        builder.Services.AddProblemDetails();
        builder.Services.AddControllers()
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

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

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();
        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");

        app.Run();
    }
}
