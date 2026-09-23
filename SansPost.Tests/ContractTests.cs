using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Posts;

namespace SansPost.Tests
{
    // Strażnicy granicy API.
    public class ContractTests
    {
        [Theory]
        [InlineData(typeof(UserResponse))]
        [InlineData(typeof(TokenResponse))]
        [InlineData(typeof(AuthenticatedUser))]
        [InlineData(typeof(PostDetailsResponse))]
        [InlineData(typeof(PostSummaryResponse))]
        [InlineData(typeof(CommentResponse))]
        public void ResponsesAndAuthResults_NeverExposePasswordHash(Type type)
        {
            Assert.Null(type.GetProperty(nameof(User.PasswordHash)));
        }

        [Theory]
        [InlineData(typeof(PostRequest))]
        [InlineData(typeof(CommentRequest))]
        [InlineData(typeof(RegisterRequest))]
        public void Requests_DoNotLetClientChooseOwner(Type type)
        {
            Assert.Null(type.GetProperty("UserId"));
        }

        [Theory]
        [InlineData("Role")]
        [InlineData("Subscription")]
        [InlineData("PasswordHash")]
        [InlineData("Id")]
        public void RegisterRequest_DoesNotAcceptServerControlledFields(string property)
        {
            Assert.Null(typeof(RegisterRequest).GetProperty(property));
        }
    }
}
