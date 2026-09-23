using SansPost.Features.Identity;

namespace SansPost.Features.Posts
{
    public class Post
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; } = null!;

        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public PostCategory Category { get; set; } = PostCategory.General;

        // Pole istniejące przed Sprintem 3; nie jest jeszcze wyświetlane w UI.
        public string? ImageUrl { get; set; }

        public DateTime CreatedAt { get; set; }

        // null = nigdy nie edytowany. Zmieniane tylko przy realnej zmianie treści.
        public DateTime? UpdatedAt { get; set; }

        // Optimistic concurrency token (ETag). Rośnie o 1 przy każdej realnej edycji.
        public int Version { get; set; } = 1;
    }
}
