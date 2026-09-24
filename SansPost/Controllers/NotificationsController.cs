using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Notifications;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Powiadomienia zalogowanego użytkownika. Brak parametru userId — tożsamość wyłącznie z JWT (ApiControllerBase).
    // Cudze powiadomienie = 404 (bez ujawniania, że istnieje). Brak DELETE w tym sprincie — historia zostaje.
    [Route("api/notifications")]
    public class NotificationsController : ApiControllerBase
    {
        private readonly INotificationService _notifications;

        public NotificationsController(INotificationService notifications)
        {
            _notifications = notifications;
        }

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] NotificationPageQuery query, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _notifications.GetAsync(userId, query, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpGet("unread-count")]
        public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            return Ok(new UnreadCountResponse(await _notifications.GetUnreadCountAsync(userId, cancellationToken)));
        }

        // Idempotentne: ponowne oznaczenie przeczytanego = nadal 200 (ReadAt z pierwszego odczytu).
        [HttpPatch("{notificationId:int}/read")]
        public async Task<IActionResult> MarkRead(int notificationId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _notifications.MarkReadAsync(userId, notificationId, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            return Ok(new MarkAllReadResponse(await _notifications.MarkAllReadAsync(userId, cancellationToken)));
        }
    }
}
