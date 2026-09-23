using Microsoft.AspNetCore.Mvc;
using SansPost.Features;

namespace SansPost.Controllers
{
    public static class ServiceResultExtensions
    {
        // Mapuje błąd use-case na odpowiedź HTTP (ProblemDetails).
        public static ObjectResult ToProblem(this ControllerBase controller, ServiceResult result)
        {
            var statusCode = result.Error switch
            {
                ServiceError.Validation => StatusCodes.Status400BadRequest,
                ServiceError.NotFound => StatusCodes.Status404NotFound,
                ServiceError.Forbidden => StatusCodes.Status403Forbidden,
                ServiceError.Conflict => StatusCodes.Status409Conflict,
                ServiceError.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
                _ => StatusCodes.Status500InternalServerError
            };

            return controller.Problem(detail: result.Message, statusCode: statusCode);
        }
    }
}
