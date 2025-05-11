using SansPost.Models.Enum;

namespace SansPost.Models
{
    public class PostRequest
    {
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Category { get; set; }
        public string? ImageUrl { get; set; }
    }
}
