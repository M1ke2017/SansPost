using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Posts;

namespace SansPost.Features.Notifications
{
    public enum NotificationType
    {
        // Jedyny typ w tym sprincie: ktoś inny skomentował post odbiorcy.
        CommentOnPost
    }

    // Powiadomienie w aplikacji. Bez gotowego tekstu i bez kopii danych — treść budowana przy odczycie z referencji
    // (aktualny przydomek aktora, aktualny tytuł i status posta), więc ukrycie treści działa także wstecz.
    public class Notification
    {
        public int Id { get; set; }

        // Odbiorca — jedyny użytkownik, który może odczytać lub oznaczyć powiadomienie.
        public int UserId { get; set; }
        public User User { get; set; } = null!;

        public NotificationType Type { get; set; }

        public int ActorUserId { get; set; }
        public User Actor { get; set; } = null!;

        public int PostId { get; set; }
        public Post Post { get; set; } = null!;

        public int? CommentId { get; set; }
        public Comment? Comment { get; set; }

        public DateTime CreatedAt { get; set; }

        // null = nieprzeczytane. Oznaczenie jest idempotentne (pierwszy odczyt zostaje).
        public DateTime? ReadAt { get; set; }
    }
}
