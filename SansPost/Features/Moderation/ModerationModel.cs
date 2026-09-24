using SansPost.Features.Identity;

namespace SansPost.Features
{
    // Treść podlegająca moderacji (Post, Comment) — wspólna logika ukrywania/przywracania.
    public interface IModeratableContent
    {
        int Id { get; }
        int UserId { get; }
        ContentStatus Status { get; set; }
        int Version { get; set; }
    }
}

namespace SansPost.Features.Moderation
{
    public enum ReportTargetType
    {
        Post,
        Comment
    }

    public enum ReportReason
    {
        Spam,
        Abuse,
        Harassment,
        InappropriateContent,
        Other
    }

    public enum ReportStatus
    {
        Pending,
        Resolved,
        Dismissed
    }

    // Zgłoszenie treści. Jeden reporter ma co najwyżej jedno Pending zgłoszenie danego zasobu —
    // częściowy UNIQUE index (reporteruserid, targettype, targetid) WHERE status = 'Pending'.
    public class Report
    {
        public int Id { get; set; }

        public int ReporterUserId { get; set; }
        public User Reporter { get; set; } = null!;

        public ReportTargetType TargetType { get; set; }
        public int TargetId { get; set; }

        public ReportReason Reason { get; set; }
        public string? Details { get; set; }
        public DateTime CreatedAt { get; set; }

        public ReportStatus Status { get; set; } = ReportStatus.Pending;
        public int? ReviewedByUserId { get; set; }
        public DateTime? ReviewedAt { get; set; }
    }

    public enum ModerationTargetType
    {
        Post,
        Comment,
        User,
        Report
    }

    public enum ModerationActionType
    {
        HidePost,
        RestorePost,
        HideComment,
        RestoreComment,
        SuspendUser,
        BanUser,
        ReactivateUser,
        DismissReport,
        ChangeRole,
        ChangeSubscription
    }

    // Niezmienny wpis audytu. Append-only: brak API edycji, a w PostgreSQL trigger odrzuca UPDATE/DELETE.
    public class ModerationAction
    {
        public int Id { get; set; }

        public int AdminUserId { get; set; }
        public User Admin { get; set; } = null!;

        public ModerationActionType ActionType { get; set; }
        public ModerationTargetType TargetType { get; set; }
        public int TargetId { get; set; }

        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
