using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using SansPost.Controller;
using SansPost.Services;
using SansPost.Models;
using System.Threading.Tasks;

namespace SansPost.Test
{
    public class UsersControllerUnitTests
    {
        [Fact]
        public async Task Register_ReturnsOk_WhenUserValid()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.RegisterUser(It.IsAny<User>())).ReturnsAsync(true);

            var controller = new UsersController(mock.Object);
            var result = await controller.Register(new User());

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Rejestracja zakończona", ok.Value!.ToString());
        }

        [Fact]
        public async Task Register_ReturnsBadRequest_WhenUserInvalid()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.RegisterUser(It.IsAny<User>())).ReturnsAsync(false);

            var controller = new UsersController(mock.Object);
            var result = await controller.Register(new User());

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Login_ReturnsOk_WhenAuthenticated()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.Authenticate("email", "pass")).ReturnsAsync("token");

            var controller = new UsersController(mock.Object);
            var result = await controller.Login(new LoginRequest { Email = "email@example.com", Password = "pass123" });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("token", ok.Value!.ToString());
        }

        [Fact]
        public async Task Login_ReturnsUnauthorized_WhenInvalid()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.Authenticate(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

            var controller = new UsersController(mock.Object);
            var result = await controller.Login(new LoginRequest { Email = "x", Password = "x" });

            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        [Fact]
        public async Task GetUser_ReturnsUser_WhenFound()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.GetUserById(10)).ReturnsAsync(new User { Id = 10 });

            var controller = new UsersController(mock.Object);
            var result = await controller.GetUser(10);

            var ok = Assert.IsType<OkObjectResult>(result);
            var user = Assert.IsType<User>(ok.Value);
            Assert.Equal(1, user.Id);
        }

        [Fact]
        public async Task GetUser_ReturnsNotFound_WhenMissing()
        {
            var mock = new Mock<UserService>(null!);
            mock.Setup(s => s.GetUserById(1)).ReturnsAsync((User?)null);

            var controller = new UsersController(mock.Object);
            var result = await controller.GetUser(1);

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }
}
