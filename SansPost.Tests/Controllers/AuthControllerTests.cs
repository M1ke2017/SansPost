using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SansPost.Controllers;
using SansPost.Features;
using SansPost.Features.Identity;

namespace SansPost.Tests.Controllers
{
    public class AuthControllerTests
    {
        private readonly Mock<IAuthService> _authService = new();
        private readonly Mock<IApiTokenService> _tokenService = new();
        private readonly Mock<IUserService> _userService = new();

        private AuthController CreateController() => new(_authService.Object, _tokenService.Object, _userService.Object);

        private static UserResponse SampleUser(int id) =>
            new(id, "user", UserRole.User, SubscriptionType.Free, DateTime.UtcNow);

        [Fact]
        public async Task Register_ReturnsCreated_WhenUserValid()
        {
            _authService.Setup(s => s.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<UserResponse>.Success(SampleUser(5)));

            var result = await CreateController().Register(new RegisterRequest(), CancellationToken.None);

            var created = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(5, Assert.IsType<UserResponse>(created.Value).Id);
        }

        [Fact]
        public async Task Register_ReturnsConflict_WhenUserAlreadyExists()
        {
            _authService.Setup(s => s.RegisterAsync(It.IsAny<RegisterRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ServiceResult<UserResponse>.Fail(ServiceError.Conflict, "exists"));

            var result = await CreateController().Register(new RegisterRequest(), CancellationToken.None);

            Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        [Fact]
        public async Task Login_ReturnsTokens_WhenAuthenticated()
        {
            var user = new AuthenticatedUser(1, "user", "user@example.com", UserRole.User);
            _authService.Setup(s => s.AuthenticateAsync("user@example.com", "pass12345", It.IsAny<CancellationToken>()))
                .ReturnsAsync(user);
            _tokenService.Setup(s => s.IssueAsync(user, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TokenResponse("access", DateTime.UtcNow.AddMinutes(15), "refresh", DateTime.UtcNow.AddDays(7)));

            var result = await CreateController().Login(new LoginRequest { Email = "user@example.com", Password = "pass12345" }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("access", Assert.IsType<TokenResponse>(ok.Value).AccessToken);
        }

        [Fact]
        public async Task Login_ReturnsUnauthorized_WhenInvalid()
        {
            _authService.Setup(s => s.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AuthenticatedUser?)null);

            var result = await CreateController().Login(new LoginRequest { Email = "x", Password = "x" }, CancellationToken.None);

            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(result).StatusCode);
            _tokenService.Verify(s => s.IssueAsync(It.IsAny<AuthenticatedUser>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
