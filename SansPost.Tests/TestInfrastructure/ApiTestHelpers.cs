using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SansPost.Features.Comments;
using SansPost.Features.Posts;

namespace SansPost.Tests.TestInfrastructure
{
    internal static class ApiTestHelpers
    {
        public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<T>(SansPostFactory.Json))!;

        public static HttpRequestMessage Request(HttpMethod method, string url, EntityTagHeaderValue? ifMatch = null, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            if (ifMatch is not null)
                request.Headers.IfMatch.Add(ifMatch);
            return request;
        }

        public static async Task<PostDetailsResponse> CreatePostAsync(this HttpClient client, string title = "Post testowy")
        {
            var response = await client.PostAsJsonAsync("/api/posts", new { title, content = "Treść posta", category = "General" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.ReadAsync<PostDetailsResponse>();
        }

        public static async Task<(CommentResponse Comment, EntityTagHeaderValue ETag)> CreateCommentAsync(this HttpClient client, int postId, string content = "Komentarz")
        {
            var response = await client.PostAsJsonAsync($"/api/posts/{postId}/comments", new { content });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.ReadAsync<CommentResponse>(), response.Headers.ETag!);
        }
    }
}
