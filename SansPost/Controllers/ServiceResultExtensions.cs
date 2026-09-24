using Microsoft.AspNetCore.Mvc;
using SansPost.Features;

namespace SansPost.Controllers
{
    public static class ServiceResultExtensions
    {
        // Mapuje błąd use-case na odpowiedź HTTP (ProblemDetails).
        // Code → type "urn:sanspost:problem:<code>" + rozszerzenie "code"; RetryAfter → nagłówek Retry-After.
        public static ObjectResult ToProblem(this ControllerBase controller, ServiceResult result)
        {
            var statusCode = result.Error switch
            {
                ServiceError.Validation => StatusCodes.Status400BadRequest,
                ServiceError.NotFound => StatusCodes.Status404NotFound,
                ServiceError.Forbidden => StatusCodes.Status403Forbidden,
                ServiceError.Conflict => StatusCodes.Status409Conflict,
                ServiceError.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
                ServiceError.RateLimited => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status500InternalServerError
            };

            var problem = controller.Problem(
                detail: result.Message,
                statusCode: statusCode,
                type: result.Code is null ? null : ProblemTypes.For(result.Code));

            if (result.Code is not null && problem.Value is ProblemDetails details)
                details.Extensions["code"] = result.Code;

            if (result.RetryAfter is { } retryAfter && controller.HttpContext is not null)
                controller.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

            return problem;
        }
    }

    public static class ProblemTypes
    {
        public static string For(string code) => $"urn:sanspost:problem:{code}";
    }
}
