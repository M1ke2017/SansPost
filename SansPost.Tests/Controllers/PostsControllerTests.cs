using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SansPost.Controllers;
using SansPost.Features;
using SansPost.Features.Posts;

namespace SansPost.Tests.Controllers
{
    public class PostsControllerTests
    {
        private static readonly PostRequest ValidRequest = new()
        {
            Title = "Test1",
            Content = "Content1",
            Category = PostCategory.General
        };

        private static PostDetailsResponse SamplePost(int id, int authorId = 1, int version = 1) =>
            new(id, "Title", "Content", PostCategory.General, null, DateTime.UtcNow, null, authorId, "author", version, 0, 0, false);

        [Fact]
        public async Task Create_ReturnsCreatedWithETag_AndUsesAuthenticatedUserAsOwner()
        {
            var service = new Mock<IPostService>();
            service.Setup(s => s.CreateAsync(7, ValidRequest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<PostDetailsResponse>.Success(SamplePost(1, authorId: 7)));

            var controller = new PostsController(service.Object).WithUser(7);
            var result = await controller.Create(ValidRequest, CancellationToken.None);

            var created = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(7, Assert.IsType<PostDetailsResponse>(created.Value).AuthorId);
            Assert.Equal("\"1\"", controller.Response.Headers.ETag.ToString());
            service.Verify(s => s.CreateAsync(7, ValidRequest, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Create_Returns429_WhenDailyPostLimitReached()
        {
            var service = new Mock<IPostService>();
            service.Setup(s => s.CreateAsync(1, ValidRequest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<PostDetailsResponse>.From(
                    SansPost.Features.Usage.DailyQuota.LimitReached(SansPost.Features.Usage.QuotaKind.Post, DateTimeOffset.UtcNow)));

            var controller = new PostsController(service.Object).WithUser(1);
            var result = await controller.Create(ValidRequest, CancellationToken.None);

            Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        [Fact]
        public async Task GetByAuthor_FiltersFeedByRouteAuthor()
        {
            var service = new Mock<IPostService>();
            service.Setup(s => s.GetFeedAsync(It.Is<PostFeedQuery>(q => q.AuthorId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<KeysetPage<PostSummaryResponse>>.Success(new KeysetPage<PostSummaryResponse>(Array.Empty<PostSummaryResponse>(), null, false)));

            var controller = new PostsController(service.Object).WithUser(null);
            var result = await controller.GetByAuthor(10, new PostFeedQuery { AuthorId = 99 }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            service.Verify(s => s.GetFeedAsync(It.Is<PostFeedQuery>(q => q.AuthorId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetById_ReturnsNotFound_WhenMissing()
        {
            var service = new Mock<IPostService>();
            service.Setup(s => s.GetByIdAsync(1, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((PostDetailsResponse?)null);

            var controller = new PostsController(service.Object).WithUser(null);
            var result = await controller.GetById(1, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Delete_ReturnsUnauthorized_WhenNoUserId()
        {
            var service = new Mock<IPostService>();

            var controller = new PostsController(service.Object).WithUser(null);
            var result = await controller.Delete(1, CancellationToken.None);

            Assert.IsType<UnauthorizedResult>(result);
            service.Verify(s => s.DeleteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Update_WithoutIfMatch_Returns428_WithoutCallingService()
        {
            var service = new Mock<IPostService>();

            var controller = new PostsController(service.Object).WithUser(1);
            var result = await controller.Update(1, ValidRequest, CancellationToken.None);

            Assert.Equal(StatusCodes.Status428PreconditionRequired, Assert.IsType<ObjectResult>(result).StatusCode);
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Delete_WithoutIfMatch_Returns428_WithoutCallingService()
        {
            var service = new Mock<IPostService>();

            var controller = new PostsController(service.Object).WithUser(1);
            var result = await controller.Delete(1, CancellationToken.None);

            Assert.Equal(StatusCodes.Status428PreconditionRequired, Assert.IsType<ObjectResult>(result).StatusCode);
            service.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Update_MapsStaleVersionTo412()
        {
            var service = new Mock<IPostService>();
            service.Setup(s => s.UpdateAsync(1, 5, 3, ValidRequest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<PostDetailsResponse>.Fail(ServiceError.PreconditionFailed, "stale"));

            var controller = new PostsController(service.Object).WithUser(1);
            controller.Request.Headers.IfMatch = "\"3\"";
            var result = await controller.Update(5, ValidRequest, CancellationToken.None);

            Assert.Equal(StatusCodes.Status412PreconditionFailed, Assert.IsType<ObjectResult>(result).StatusCode);
        }
    }
}
