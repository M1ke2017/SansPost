using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Wszystkie endpointy moderacji: wyłącznie Admin (polityka ApiAdmin, JWT) — server-side, nie tylko ukryty przycisk.
    [Route("api/moderation")]
    [Authorize(Policy = AuthPolicies.ApiAdmin)]
    public class ModerationController : ApiControllerBase
    {
        private readonly ModerationService _moderation;
        private readonly ReportService _reports;

        public ModerationController(ModerationService moderation, ReportService reports)
        {
            _moderation = moderation;
            _reports = reports;
        }

        private int AdminId => User.GetUserId()!.Value;

        [HttpGet("reports")]
        public async Task<IActionResult> PendingReports([FromQuery] ModerationQueueQuery query, CancellationToken cancellationToken)
        {
            var result = await _reports.GetPendingAsync(query, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpPost("reports/{reportId:int}/dismiss")]
        public Task<IActionResult> Dismiss(int reportId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.DismissReportAsync(AdminId, reportId, request.Reason, cancellationToken));

        [HttpGet("posts/{postId:int}")]
        public async Task<IActionResult> GetPost(int postId, CancellationToken cancellationToken)
        {
            var view = await _moderation.GetPostAsync(postId, cancellationToken);
            return view is null ? NotFound() : Ok(view);
        }

        [HttpPost("posts/{postId:int}/hide")]
        public Task<IActionResult> HidePost(int postId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.HidePostAsync(AdminId, postId, request.Reason, cancellationToken));

        [HttpPost("posts/{postId:int}/restore")]
        public Task<IActionResult> RestorePost(int postId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.RestorePostAsync(AdminId, postId, request.Reason, cancellationToken));

        [HttpGet("comments/{commentId:int}")]
        public async Task<IActionResult> GetComment(int commentId, CancellationToken cancellationToken)
        {
            var view = await _moderation.GetCommentAsync(commentId, cancellationToken);
            return view is null ? NotFound() : Ok(view);
        }

        [HttpPost("comments/{commentId:int}/hide")]
        public Task<IActionResult> HideComment(int commentId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.HideCommentAsync(AdminId, commentId, request.Reason, cancellationToken));

        [HttpPost("comments/{commentId:int}/restore")]
        public Task<IActionResult> RestoreComment(int commentId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.RestoreCommentAsync(AdminId, commentId, request.Reason, cancellationToken));

        [HttpPost("users/{userId:int}/suspend")]
        public Task<IActionResult> Suspend(int userId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.SuspendUserAsync(AdminId, userId, request.Reason, cancellationToken));

        [HttpPost("users/{userId:int}/ban")]
        public Task<IActionResult> Ban(int userId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.BanUserAsync(AdminId, userId, request.Reason, cancellationToken));

        [HttpPost("users/{userId:int}/reactivate")]
        public Task<IActionResult> Reactivate(int userId, ModerationDecisionRequest request, CancellationToken cancellationToken) =>
            Execute(_moderation.ReactivateUserAsync(AdminId, userId, request.Reason, cancellationToken));

        [HttpGet("actions")]
        public async Task<IActionResult> Actions([FromQuery] ModerationQueueQuery query, CancellationToken cancellationToken)
        {
            var result = await _moderation.GetActionsAsync(query, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        private async Task<IActionResult> Execute(Task<Features.ServiceResult> action)
        {
            var result = await action;
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }
    }

    [Route("api/reports")]
    public class ReportsController : ApiControllerBase
    {
        private readonly ReportService _reports;

        public ReportsController(ReportService reports)
        {
            _reports = reports;
        }

        // 201 = nowe zgłoszenie, 200 = to samo zgłoszenie już czeka na moderację (idempotentnie).
        [HttpPost]
        public async Task<IActionResult> Create(CreateReportRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _reports.CreateAsync(userId, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            return result.Value!.Created
                ? StatusCode(StatusCodes.Status201Created, result.Value.Report)
                : Ok(result.Value.Report);
        }
    }

    // Minimalny publiczny status instancji — bez liczby kont, emaili ani danych administratora.
    [Route("api/public")]
    public class PublicController : ApiControllerBase
    {
        private readonly IAuthService _authService;

        public PublicController(IAuthService authService)
        {
            _authService = authService;
        }

        public sealed record PublicStatusResponse(bool RegistrationAvailable);

        [AllowAnonymous]
        [HttpGet("status")]
        public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
            Ok(new PublicStatusResponse(await _authService.IsRegistrationAvailableAsync(cancellationToken)));
    }
}
