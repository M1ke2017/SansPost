using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Moderation
{
    public interface IReportService
    {
        // Zgłoszenie publicznej treści przez aktywnego użytkownika. Idempotentne dla trwającego (Pending) zgłoszenia.
        Task<ServiceResult<ReportSubmission>> CreateAsync(int reporterUserId, CreateReportRequest request, CancellationToken cancellationToken = default);

        // Kolejka moderacji: Pending, od najstarszych (FIFO), keyset (CreatedAt, Id).
        Task<ServiceResult<KeysetPage<ModerationQueueItem>>> GetPendingAsync(ModerationQueueQuery query, CancellationToken cancellationToken = default);
    }

    public class ReportService : IReportService
    {
        private const string CursorScope = "reports.pending";

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly IWriteGuard _writeGuard;

        public ReportService(ApplicationDbContext context, TimeProvider time, IWriteGuard writeGuard)
        {
            _context = context;
            _time = time;
            _writeGuard = writeGuard;
        }

        public async Task<ServiceResult<ReportSubmission>> CreateAsync(int reporterUserId, CreateReportRequest request, CancellationToken cancellationToken = default)
        {
            if (RequestValidator.Validate(request) is { } error)
                return ServiceResult<ReportSubmission>.Fail(ServiceError.Validation, error);
            if (!Enum.IsDefined(request.TargetType!.Value) || !Enum.IsDefined(request.Reason!.Value))
                return ServiceResult<ReportSubmission>.Fail(ServiceError.Validation, "Nieprawidłowy typ zasobu lub powód.");
            if (await _writeGuard.CheckAsync(reporterUserId, cancellationToken) is { } denied)
                return ServiceResult<ReportSubmission>.From(denied);

            var targetType = request.TargetType.Value;
            if (!await IsPubliclyVisibleAsync(targetType, request.TargetId, cancellationToken))
                return ServiceResult<ReportSubmission>.Fail(ServiceError.NotFound, "Zgłaszany zasób nie istnieje.");

            var details = string.IsNullOrWhiteSpace(request.Details) ? string.Empty : request.Details.Trim();
            var now = _time.GetUtcNow().UtcDateTime;

            // Jeden atomowy INSERT: przy trwającym zgłoszeniu tego samego zasobu przez tego samego użytkownika
            // częściowy UNIQUE index rozstrzyga konflikt w bazie (bez wyjątku) — równoległe requesty nie tworzą duplikatów.
            var inserted = await _context.Database.SqlQuery<int>($"""
                INSERT INTO reports (reporteruserid, targettype, targetid, reason, details, createdat, status)
                VALUES ({reporterUserId}, {targetType.ToString()}, {request.TargetId}, {request.Reason.Value.ToString()}, NULLIF({details}, ''), {now}, 'Pending')
                ON CONFLICT (reporteruserid, targettype, targetid) WHERE status = 'Pending' DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);

            var report = await _context.Reports
                .AsNoTracking()
                .Where(r => r.ReporterUserId == reporterUserId && r.TargetType == targetType && r.TargetId == request.TargetId
                    && r.Status == ReportStatus.Pending)
                .Select(r => new ReportResponse(r.Id, r.TargetType, r.TargetId, r.Reason, r.Status, r.CreatedAt))
                .FirstAsync(cancellationToken);

            return ServiceResult<ReportSubmission>.Success(new ReportSubmission(report, Created: inserted.Count > 0));
        }

        public async Task<ServiceResult<KeysetPage<ModerationQueueItem>>> GetPendingAsync(ModerationQueueQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > ModerationLimits.MaxPageSize)
                return ServiceResult<KeysetPage<ModerationQueueItem>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{ModerationLimits.MaxPageSize}.");

            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, CursorScope, out cursor))
                return ServiceResult<KeysetPage<ModerationQueueItem>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            var reports = _context.Reports.AsNoTracking().Where(r => r.Status == ReportStatus.Pending);
            if (cursor is not null)
                reports = reports.Where(r => r.CreatedAt >= cursor.CreatedAt && (r.CreatedAt > cursor.CreatedAt || r.Id > cursor.Id));

            // Jedno zapytanie: dane zgłoszenia + podgląd i autor celu (skorelowane podzapytania zależne od typu celu).
            var page = await reports
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(query.Limit + 1)
                .Select(r => new ModerationQueueItem(
                    r.Id,
                    r.TargetType,
                    r.TargetId,
                    r.Reason,
                    r.Details,
                    r.CreatedAt,
                    r.Reporter.Username,
                    r.TargetType == ReportTargetType.Post
                        ? _context.Posts.Where(p => p.Id == r.TargetId).Select(p => p.Title).FirstOrDefault()
                        : _context.Comments.Where(c => c.Id == r.TargetId)
                            .Select(c => c.Content.Length > ModerationLimits.PreviewLength ? c.Content.Substring(0, ModerationLimits.PreviewLength) : c.Content)
                            .FirstOrDefault(),
                    r.TargetType == ReportTargetType.Post
                        ? _context.Posts.Where(p => p.Id == r.TargetId).Select(p => (ContentStatus?)p.Status).FirstOrDefault()
                        : _context.Comments.Where(c => c.Id == r.TargetId).Select(c => (ContentStatus?)c.Status).FirstOrDefault(),
                    r.TargetType == ReportTargetType.Post
                        ? _context.Posts.Where(p => p.Id == r.TargetId).Select(p => p.User.Username).FirstOrDefault()
                        : _context.Comments.Where(c => c.Id == r.TargetId).Select(c => c.User.Username).FirstOrDefault()))
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new KeysetCursor(CursorScope, page[^1].CreatedAt, page[^1].ReportId).Encode() : null;
            return ServiceResult<KeysetPage<ModerationQueueItem>>.Success(new KeysetPage<ModerationQueueItem>(page, nextCursor, hasMore));
        }

        // Zgłaszać można tylko to, co jest publicznie widoczne (ukryte/usunięte = 404, bez zdradzania istnienia).
        private Task<bool> IsPubliclyVisibleAsync(ReportTargetType targetType, int targetId, CancellationToken cancellationToken) =>
            targetType == ReportTargetType.Post
                ? _context.Posts.AnyAsync(p => p.Id == targetId && p.Status == ContentStatus.Published, cancellationToken)
                : _context.Comments.AnyAsync(c => c.Id == targetId && c.Status == ContentStatus.Published
                    && c.Post.Status == ContentStatus.Published, cancellationToken);
    }
}
