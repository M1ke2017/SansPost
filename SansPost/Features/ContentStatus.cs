namespace SansPost.Features
{
    // Stan treści (Post, Comment). Zapisywany jako nazwa + CHECK constraint.
    public enum ContentStatus
    {
        // Publiczne — jedyny stan widoczny we wszystkich publicznych read modelach.
        Published,

        // Ukryte przez moderatora. Moderator może przywrócić (Hidden → Published).
        Hidden,

        // Usunięte przez autora (soft delete). Nie jest przywracane przez zwykłe "restore" moderacji.
        Deleted
    }
}
