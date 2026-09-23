using System.Text.Json.Serialization;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
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
        builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IPostService, PostService>();
        builder.Services.AddScoped<ICommentService, CommentService>();
        builder.Services.AddScoped<ILikeService, LikeService>();
        builder.Services.AddScoped<IProfileService, ProfileService>();
        builder.Services.AddScoped<ISearchService, PostgresSearchService>();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            // Wyjątki jako ogólny ProblemDetails 500 — bez komunikatu i stack trace.
            app.UseExceptionHandler();
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
