using System.Net.Http.Json;
using Microsoft.Playwright;

namespace SansPost.E2E.Infrastructure
{
    // Zalogowana karta przeglądarki (osobny kontekst = osobne cookie) dla użytkownika testowego lub admina.
    public sealed class Session : IAsyncDisposable
    {
        private Session(IBrowserContext context, IPage page, SansPostServer.TestUser? user)
        {
            Context = context;
            Page = page;
            User = user;
        }

        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public SansPostServer.TestUser? User { get; }

        public static async Task<Session> UserAsync(E2EEnvironment env, SansPostServer? server = null, string? alias = null,
            int width = 1280, int height = 800, bool reducedMotion = false)
        {
            server ??= env.Main;
            var user = await server.CreateUserAsync(alias);
            var context = await env.NewContextAsync(server, width, height, reducedMotion: reducedMotion);
            var page = await context.NewPageAsync();
            await Ui.LoginAsync(page, user.Email, E2EEnvironment.UserPassword);
            return new Session(context, page, user);
        }

        public static async Task<Session> AdminAsync(E2EEnvironment env, SansPostServer? server = null, int width = 1280, int height = 800)
        {
            var context = await env.NewContextAsync(server ?? env.Main, width, height);
            var page = await context.NewPageAsync();
            await Ui.LoginAsync(page, E2EEnvironment.AdminEmail, E2EEnvironment.AdminPassword);
            return new Session(context, page, null);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    public static class Api
    {
        public sealed record Created(int Id);

        public static async Task<int> CreatePostAsync(SansPostServer server, SansPostServer.TestUser user, string title,
            string category = "General", string content = "Treść posta testowego E2E.")
        {
            using var api = await server.CreateAuthenticatedApiClientAsync(user.Email, E2EEnvironment.UserPassword);
            var response = await api.PostAsJsonAsync("/api/posts", new { title, content, category });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
        }

        public static async Task<int> CommentAsync(SansPostServer server, SansPostServer.TestUser user, int postId, string content)
        {
            using var api = await server.CreateAuthenticatedApiClientAsync(user.Email, E2EEnvironment.UserPassword);
            var response = await api.PostAsJsonAsync($"/api/posts/{postId}/comments", new { content });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
        }

        public static async Task AdminAsync(SansPostServer server, string path)
        {
            using var api = await server.CreateAdminApiClientAsync();
            var response = await api.PostAsJsonAsync(path, new { reason = "E2E" });
            response.EnsureSuccessStatusCode();
        }
    }
}
