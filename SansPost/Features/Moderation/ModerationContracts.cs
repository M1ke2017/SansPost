using System.ComponentModel.DataAnnotations;
using SansPost.Features.Identity;

namespace SansPost.Features.Moderation
{
    public static class ModerationLimits
    {
        public const int DetailsMaxLength = 500;
        public const int ReasonMaxLength = 500;
        public const int PreviewLength = 200;
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;
    }

    // Reporter wynika z zaufanej tożsamości — nie ma go w requeście.
    public sealed class CreateReportRequest
    {
        [Required(ErrorMessage = "Typ zgłaszanego zasobu jest wymagany.")]
        public ReportTargetType? TargetType { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Nieprawidłowy identyfikator zasobu.")]
        public int TargetId { get; set; }

        [Required(ErrorMessage = "Powód zgłoszenia jest wymagany.")]
        public ReportReason? Reason { get; set; }

        [StringLength(ModerationLimits.DetailsMaxLength, ErrorMessage = "Szczegóły mogą mieć maksymalnie 500 znaków.")]
        public string? Details { get; set; }
    }

    public sealed record ReportResponse(int Id, ReportTargetType TargetType, int TargetId, ReportReason Reason, ReportStatus Status, DateTime CreatedAt);

    // Created = false: to samo aktywne zgłoszenie już istniało (idempotentnie, bez duplikatu).
    public sealed record ReportSubmission(ReportResponse Report, bool Created);

    public sealed class ModerationQueueQuery
    {
        public string? Cursor { get; set; }
        public int Limit { get; set; } = ModerationLimits.DefaultPageSize;
    }

    // Element kolejki moderacji — tylko to, czego potrzebuje moderator (bez emaili, IP, danych auth).
    public sealed record ModerationQueueItem(
        int ReportId,
        ReportTargetType TargetType,
        int TargetId,
        ReportReason Reason,
        string? Details,
        DateTime CreatedAt,
        string ReporterUsername,
        string? TargetPreview,
        ContentStatus? TargetStatus,
        string? TargetAuthorUsername);

    // Uzasadnienie decyzji zapisywane w audycie.
    public sealed class ModerationDecisionRequest
    {
        [StringLength(ModerationLimits.ReasonMaxLength, ErrorMessage = "Uzasadnienie może mieć maksymalnie 500 znaków.")]
        public string? Reason { get; set; }
    }

    // Widok treści dla moderatora — także Hidden i (do audytu) Deleted.
    public sealed record ModeratedContentView(
        ReportTargetType Type,
        int Id,
        int? PostId,
        string? Title,
        string Content,
        ContentStatus Status,
        int Version,
        int AuthorId,
        string AuthorUsername,
        AccountStatus AuthorStatus,
        DateTime CreatedAt,
        DateTime? DeletedAt,
        int PendingReports);

    public sealed record ModerationActionResponse(
        int Id,
        int AdminUserId,
        string AdminUsername,
        ModerationActionType ActionType,
        ModerationTargetType TargetType,
        int TargetId,
        string? Reason,
        DateTime CreatedAt);
}
