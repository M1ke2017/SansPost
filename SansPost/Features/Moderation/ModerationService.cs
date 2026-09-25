using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using SansPost.Infrastructure.Hosting;

namespace SansPost.Features.Moderation
{
    // Akcje administratora. Każda zmiana stanu zapisuje niezmienny wpis ModerationAction w tej samej transakcji.
    // Autoryzacja: polityka ApiAdmin (REST) / [Authorize(Roles = Admin)] (Blazor) ORAZ ponowne sprawdzenie w bazie tutaj.
    public class ModerationService
    {
        private const string ActionsCursorScope = "moderation.actions";

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;

        private readonly ILogger<ModerationService> _logger;

        public ModerationService(ApplicationDbContext context, TimeProvider time, ILogger<ModerationService>? logger = null)
        {
            _context = context;
            _time = time;
            _logger = logger ?? NullLogger<ModerationService>.Instance;
        }

        private DateTime Now => _time.GetUtcNow().UtcDateTime;

        // ---------------- Zgłoszenia ----------------

        // Jeden SaveChanges (UPDATE reports + INSERT audit) jest atomowy sam w sobie — jawna transakcja zbędna.
        public async Task<ServiceResult> DismissReportAsync(int adminUserId, int reportId, string? reason, CancellationToken cancellationToken = default)
        {
            if (await EnsureAdminAsync(adminUserId, reason, cancellationToken) is { } denied)
                return denied;

            var report = await _context.Reports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
            if (report is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Zgłoszenie nie istnieje.");
            if (report.Status != ReportStatus.Pending)
                return ServiceResult.Fail(ServiceError.Conflict, "Zgłoszenie zostało już rozpatrzone.", "report-already-reviewed");

            report.Status = ReportStatus.Dismissed;
            report.ReviewedByUserId = adminUserId;
            report.ReviewedAt = Now;
            Audit(adminUserId, ModerationActionType.DismissReport, ModerationTargetType.Report, reportId, reason);

            await _context.SaveChangesAsync(cancellationToken);
            return ServiceResult.Success();
        }

        // ---------------- Treści ----------------

        public Task<ServiceResult> HidePostAsync(int adminUserId, int postId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeContentStatusAsync(_context.Posts, ReportTargetType.Post, ModerationTargetType.Post, ModerationActionType.HidePost,
                adminUserId, postId, hide: true, reason, cancellationToken);

        public Task<ServiceResult> RestorePostAsync(int adminUserId, int postId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeContentStatusAsync(_context.Posts, ReportTargetType.Post, ModerationTargetType.Post, ModerationActionType.RestorePost,
                adminUserId, postId, hide: false, reason, cancellationToken);

        public Task<ServiceResult> HideCommentAsync(int adminUserId, int commentId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeContentStatusAsync(_context.Comments, ReportTargetType.Comment, ModerationTargetType.Comment, ModerationActionType.HideComment,
                adminUserId, commentId, hide: true, reason, cancellationToken);

        public Task<ServiceResult> RestoreCommentAsync(int adminUserId, int commentId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeContentStatusAsync(_context.Comments, ReportTargetType.Comment, ModerationTargetType.Comment, ModerationActionType.RestoreComment,
                adminUserId, commentId, hide: false, reason, cancellationToken);

        // Jawna transakcja JEST tu uzasadniona: dwa polecenia (masowe zamknięcie zgłoszeń + zmiana stanu/audyt)
        // muszą się udać razem albo wcale. Brak Commit (konflikt, wyjątek, anulowanie) = rollback przy Dispose.
        private async Task<ServiceResult> ChangeContentStatusAsync<TContent>(
            DbSet<TContent> contents, ReportTargetType reportTarget, ModerationTargetType auditTarget, ModerationActionType action,
            int adminUserId, int contentId, bool hide, string? reason, CancellationToken cancellationToken)
            where TContent : class, IModeratableContent
        {
            if (await EnsureAdminAsync(adminUserId, reason, cancellationToken) is { } denied)
                return denied;

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var content = await contents.FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);
            if (content is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Treść nie istnieje.");

            // Hidden (moderacja) i Deleted (autor) to różne stany: moderator nie "przywraca" treści usuniętej przez autora.
            if (content.Status == ContentStatus.Deleted)
                return ServiceResult.Fail(ServiceError.Conflict, "Treść została usunięta przez autora i nie podlega tej operacji.", "content-deleted-by-author");
            if (hide && content.Status == ContentStatus.Hidden)
                return ServiceResult.Fail(ServiceError.Conflict, "Treść jest już ukryta.", "content-already-hidden");
            if (!hide && content.Status == ContentStatus.Published)
                return ServiceResult.Fail(ServiceError.Conflict, "Treść nie jest ukryta.", "content-not-hidden");

            var now = Now;

            // 1) Ukrycie rozstrzyga wszystkie trwające zgłoszenia tej treści.
            if (hide)
            {
                await _context.Reports
                    .Where(r => r.TargetType == reportTarget && r.TargetId == contentId && r.Status == ReportStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, ReportStatus.Resolved)
                        .SetProperty(r => r.ReviewedByUserId, adminUserId)
                        .SetProperty(r => r.ReviewedAt, now), cancellationToken);
            }

            // 2) Zmiana stanu (UPDATE ... WHERE version — konflikt z równoległą edycją autora) + wpis audytu.
            content.Status = hide ? ContentStatus.Hidden : ContentStatus.Published;
            content.Version++;
            Audit(adminUserId, action, auditTarget, contentId, reason);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ServiceResult.Fail(ServiceError.Conflict, "Treść została w międzyczasie zmieniona. Spróbuj ponownie.", "content-changed");
            }

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult.Success();
        }

        // ---------------- Konta ----------------

        public Task<ServiceResult> SuspendUserAsync(int adminUserId, int userId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeAccountStatusAsync(adminUserId, userId, AccountStatus.Suspended, ModerationActionType.SuspendUser, reason, cancellationToken);

        public Task<ServiceResult> BanUserAsync(int adminUserId, int userId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeAccountStatusAsync(adminUserId, userId, AccountStatus.Banned, ModerationActionType.BanUser, reason, cancellationToken);

        public Task<ServiceResult> ReactivateUserAsync(int adminUserId, int userId, string? reason, CancellationToken cancellationToken = default) =>
            ChangeAccountStatusAsync(adminUserId, userId, AccountStatus.Active, ModerationActionType.ReactivateUser, reason, cancellationToken);

        // Jawna transakcja: zmiana statusu + nowa AuthVersion + masowe unieważnienie refresh tokenów + audyt.
        private async Task<ServiceResult> ChangeAccountStatusAsync(
            int adminUserId, int userId, AccountStatus status, ModerationActionType action, string? reason, CancellationToken cancellationToken)
        {
            if (await EnsureAdminAsync(adminUserId, reason, cancellationToken) is { } denied)
                return denied;
            if (userId == adminUserId)
                return ServiceResult.Fail(ServiceError.Validation, "Nie możesz moderować własnego konta.");

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");
            if (user.Role == UserRole.Admin)
                return ServiceResult.Fail(ServiceError.Forbidden, "Najpierw odbierz rolę administratora.", "target-is-admin");
            if (user.Status == status)
                return ServiceResult.Fail(ServiceError.Conflict, "Konto ma już ten status.", "account-status-unchanged");

            user.Status = status;
            user.AuthVersion++; // istniejące cookie i access tokeny są odrzucane przy następnym żądaniu

            if (status != AccountStatus.Active)
                await RevokeRefreshTokensAsync(userId, cancellationToken);

            Audit(adminUserId, action, ModerationTargetType.User, userId, reason);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult.Success();
        }

        public async Task<ServiceResult> ChangeRoleAsync(int adminUserId, int userId, UserRole role, CancellationToken cancellationToken = default)
        {
            if (await EnsureAdminAsync(adminUserId, null, cancellationToken) is { } denied)
                return denied;
            if (!Enum.IsDefined(role))
                return ServiceResult.Fail(ServiceError.Validation, "Nieprawidłowa rola użytkownika.");
            if (userId == adminUserId)
                return ServiceResult.Fail(ServiceError.Validation, "Nie możesz zmienić własnej roli.");

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");
            if (user.Role == role)
                return ServiceResult.Success();

            var previous = user.Role;
            user.Role = role;
            user.AuthVersion++; // sesje ze starą rolą (cookie, JWT) są odrzucane przy następnym żądaniu
            await RevokeRefreshTokensAsync(userId, cancellationToken);

            Audit(adminUserId, ModerationActionType.ChangeRole, ModerationTargetType.User, userId, $"{previous} → {role}");
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult.Success();
        }

        // Jeden SaveChanges (subskrypcja + audyt) — atomowy bez jawnej transakcji.
        public async Task<ServiceResult> SetSubscriptionAsync(int adminUserId, int userId, SubscriptionType type, DateTime? expiresAt, CancellationToken cancellationToken = default)
        {
            if (await EnsureAdminAsync(adminUserId, null, cancellationToken) is { } denied)
                return denied;
            if (!Enum.IsDefined(type))
                return ServiceResult.Fail(ServiceError.Validation, "Nieprawidłowy typ subskrypcji.");

            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");

            var expiresAtUtc = expiresAt?.ToUniversalTime();
            if (user.Subscription is null)
            {
                user.Subscription = new Subscription { Type = type, ExpiresAt = expiresAtUtc };
            }
            else
            {
                user.Subscription.Type = type;
                user.Subscription.ExpiresAt = expiresAtUtc;
            }

            Audit(adminUserId, ModerationActionType.ChangeSubscription, ModerationTargetType.User, userId,
                expiresAtUtc is null ? type.ToString() : $"{type} do {expiresAtUtc:u}");
            await _context.SaveChangesAsync(cancellationToken);
            return ServiceResult.Success();
        }

        // ---------------- Odczyty administratora ----------------

        public Task<ModeratedContentView?> GetPostAsync(int postId, CancellationToken cancellationToken = default) =>
            _context.Posts
                .AsNoTracking()
                .Where(p => p.Id == postId)
                .Select(p => new ModeratedContentView(
                    ReportTargetType.Post, p.Id, null, p.Title, p.Content, p.Status, p.Version,
                    p.UserId, p.User.Username, p.User.Status, p.CreatedAt, p.DeletedAt,
                    _context.Reports.Count(r => r.TargetType == ReportTargetType.Post && r.TargetId == p.Id && r.Status == ReportStatus.Pending)))
                .FirstOrDefaultAsync(cancellationToken);

        public Task<ModeratedContentView?> GetCommentAsync(int commentId, CancellationToken cancellationToken = default) =>
            _context.Comments
                .AsNoTracking()
                .Where(c => c.Id == commentId)
                .Select(c => new ModeratedContentView(
                    ReportTargetType.Comment, c.Id, c.PostId, null, c.Content, c.Status, c.Version,
                    c.UserId, c.User.Username, c.User.Status, c.CreatedAt, c.DeletedAt,
                    _context.Reports.Count(r => r.TargetType == ReportTargetType.Comment && r.TargetId == c.Id && r.Status == ReportStatus.Pending)))
                .FirstOrDefaultAsync(cancellationToken);

        public async Task<ServiceResult<KeysetPage<ModerationActionResponse>>> GetActionsAsync(ModerationQueueQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > ModerationLimits.MaxPageSize)
                return ServiceResult<KeysetPage<ModerationActionResponse>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{ModerationLimits.MaxPageSize}.");

            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, ActionsCursorScope, out cursor))
                return ServiceResult<KeysetPage<ModerationActionResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            var actions = _context.ModerationActions.AsNoTracking();
            if (cursor is not null)
                actions = actions.Where(a => a.CreatedAt <= cursor.CreatedAt && (a.CreatedAt < cursor.CreatedAt || a.Id < cursor.Id));

            var page = await actions
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(query.Limit + 1)
                .Select(a => new ModerationActionResponse(a.Id, a.AdminUserId, a.Admin.Username, a.ActionType, a.TargetType, a.TargetId, a.Reason, a.CreatedAt))
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new KeysetCursor(ActionsCursorScope, page[^1].CreatedAt, page[^1].Id).Encode() : null;
            return ServiceResult<KeysetPage<ModerationActionResponse>>.Success(new KeysetPage<ModerationActionResponse>(page, nextCursor, hasMore));
        }

        // ---------------- Wspólne ----------------

        // Obrona w głębi: niezależnie od punktu wejścia (REST, Blazor) wykonawca musi być aktywnym Adminem w bazie.
        private async Task<ServiceResult?> EnsureAdminAsync(int adminUserId, string? reason, CancellationToken cancellationToken)
        {
            if (reason is { Length: > ModerationLimits.ReasonMaxLength })
                return ServiceResult.Fail(ServiceError.Validation, "Uzasadnienie może mieć maksymalnie 500 znaków.");

            var isAdmin = await _context.Users.AnyAsync(
                u => u.Id == adminUserId && u.Role == UserRole.Admin && u.Status == AccountStatus.Active, cancellationToken);

            return isAdmin ? null : ServiceResult.Fail(ServiceError.Forbidden, "Operacja wymaga uprawnień administratora.");
        }

        // Log operatora (bez treści uzasadnienia — może zawierać dane osobowe) + metryka; wpis w dzienniku audytu w tej samej transakcji.
        private void Audit(int adminUserId, ModerationActionType action, ModerationTargetType target, int targetId, string? reason)
        {
            _logger.LogInformation("Moderation {Action} on {TargetType} {TargetId} by admin {AdminUserId}.", action, target, targetId, adminUserId);
            SansPostTelemetry.ModerationActions.Add(1, new KeyValuePair<string, object?>("action", action.ToString()));
            _context.ModerationActions.Add(new ModerationAction
            {
                AdminUserId = adminUserId,
                ActionType = action,
                TargetType = target,
                TargetId = targetId,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                CreatedAt = Now
            });
        }

        private Task RevokeRefreshTokensAsync(int userId, CancellationToken cancellationToken)
        {
            var now = Now;
            return _context.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }
    }
}
