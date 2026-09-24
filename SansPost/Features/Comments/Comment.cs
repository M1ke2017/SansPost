using SansPost.Features.Identity;
using SansPost.Features.Posts;

namespace SansPost.Features.Comments
{
    public class Comment : IModeratableContent
    {
        public int Id { get; set; }

        public int PostId { get; set; }
        public Post Post { get; set; } = null!;

        public int UserId { get; set; }
        public User User { get; set; } = null!;

        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        // null = nigdy nie edytowany. Zmieniane tylko przy realnej zmianie treści.
        public DateTime? UpdatedAt { get; set; }

        // Optimistic concurrency token (ETag). Rośnie o 1 przy każdej realnej edycji i zmianie stanu.
        public int Version { get; set; } = 1;

        // Tylko Published (na opublikowanym poście) jest widoczne publicznie.
        public ContentStatus Status { get; set; } = ContentStatus.Published;
        public DateTime? DeletedAt { get; set; }
    }
}
