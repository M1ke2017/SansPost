using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using SansPost.Controller;
using SansPost.Services;
using SansPost.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SansPost.Test
{
    public class PostsControllerUnitTests
    {
        [Fact]
        public async Task AddPost_ReturnsOk_WhenSuccess()
        {
            var mock = new Mock<PostService>(null!, null!);
            mock.Setup(s => s.AddPost(1, "Test1", "Content1", "Category1", null)).ReturnsAsync(true);

            var controller = new PostsController(mock.Object);
            var result = await controller.AddPost(new PostRequest
            {
                UserId = 1,
                Title = "Test1",
                Content = "Content1",
                Category = "Category1",
                ImageUrl = null
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("dodany", ok.Value!.ToString());
        }

        [Fact]
        public async Task AddPost_ReturnsBadRequest_WhenFail()
        {
            var mock = new Mock<PostService>(null!, null!);
            mock.Setup(s => s.AddPost(1, "Text", "Contnet", "Category", null)).ReturnsAsync(false);

            var controller = new PostsController(mock.Object);
            var result = await controller.AddPost(new PostRequest
            {
                UserId = 1,
                Title = "Text",
                Content = "Content",
                Category = "Category",
                ImageUrl = null
            });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task GetUserPosts_ReturnsList()
        {
            var mock = new Mock<PostService>(null!, null!);
            mock.Setup(s => s.GetUserPosts(10)).ReturnsAsync(new List<Post> { new Post { Id = 10 } });

            var controller = new PostsController(mock.Object);
            var result = await controller.GetUserPosts(1);

            var ok = Assert.IsType<OkObjectResult>(result);
            var list = Assert.IsType<List<Post>>(ok.Value);
            Assert.Single(list);
        }

        [Fact]
        public async Task GetPost_ReturnsNotFound_WhenMissing()
        {
            var mock = new Mock<PostService>(null!, null!);
            mock.Setup(s => s.GetPostById(1)).ReturnsAsync((Post?)null);

            var controller = new PostsController(mock.Object);
            var result = await controller.GetPost(1);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task DeletePost_ReturnsUnauthorized_WhenNoUserId()
        {
            var mock = new Mock<PostService>(null!, null!);
            var controller = new PostsController(mock.Object);

            var result = await controller.DeletePost(1);
            Assert.IsType<UnauthorizedObjectResult>(result);
        }
    }
}
