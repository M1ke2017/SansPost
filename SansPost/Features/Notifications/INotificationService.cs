using SansPost.Features.Comments;

namespace SansPost.Features.Notifications
{
    // Granica modułu Notifications. Moduły-producenci (Comments) znają tylko ten interfejs.
    //
    // Dziś: Stage* dodaje powiadomienie do TEGO SAMEGO DbContext co zdarzenie źródłowe — zapis jednym SaveChanges
    // (atomowo: nie ma powiadomienia bez komentarza ani odwrotnie).
    // Później (poza zakresem): Stage* może zapisywać zdarzenie integracyjne do Outbox w tej samej transakcji,
    // a osobny Notifications Worker (broker) zbuduje powiadomienie — kontrakt producenta się nie zmienia.
    public interface INotificationService
    {
        // Rejestruje (bez zapisu) powiadomienie "komentarz pod Twoim postem". Nic nie robi dla własnego posta.
        void StageCommentOnPost(int postAuthorId, Comment comment);

        Task<ServiceResult<KeysetPage<NotificationResponse>>> GetAsync(int userId, NotificationPageQuery query, CancellationToken cancellationToken = default);

        Task<int> GetUnreadCountAsync(int userId, CancellationToken cancellationToken = default);

        // Idempotentne. NotFound także dla cudzego powiadomienia (bez ujawniania, że istnieje).
        Task<ServiceResult<NotificationResponse>> MarkReadAsync(int userId, int notificationId, CancellationToken cancellationToken = default);

        // Zwraca liczbę faktycznie oznaczonych (0 przy powtórzeniu).
        Task<int> MarkAllReadAsync(int userId, CancellationToken cancellationToken = default);
    }
}
