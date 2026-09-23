using Microsoft.AspNetCore.Mvc;
using Moq;
using SansPost.Controllers;
using SansPost.Features.Identity;

namespace SansPost.Tests.Controllers
{
    public class UsersControllerTests
    {
        private readonly Mock<IUserService> _userService = new();

        private UsersController CreateController() => new(_userService.Object);

        private static UserResponse SampleUser(int id) =>
            new(id, "user", UserRole.User, SubscriptionType.Free, DateTime.UtcNow);

        [Fact]
        public async Task GetUser_ReturnsUser_WhenFound()
        {
            _userService.Setup(s => s.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(SampleUser(10));

            var result = await CreateController().GetUser(10, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(10, Assert.IsType<UserResponse>(ok.Value).Id);
        }

        [Fact]
        public async Task GetUser_ReturnsNotFound_WhenMissing()
        {
            _userService.Setup(s => s.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((UserResponse?)null);

            var result = await CreateController().GetUser(1, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }
    }
}
