using SansPost.Features.Identity;
using SansPost.Features.Posts;

namespace SansPost.Features.Reactions
{
    public class Like
    {
        public int Id { get; set; }

        public int PostId { get; set; }
        public Post Post { get; set; } = null!;

        public int UserId { get; set; }
        public User User { get; set; } = null!;
    }
}
