using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SansPost.Infrastructure.Security
{
    // REST: wspólny SearchRateLimiter (ten sam, którego używa wyszukiwarka Blazor). Przekroczenie → 429 + Retry-After;
    // treść ProblemDetails dopisuje UseStatusCodePages dla /api.
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SearchRateLimitAttribute : Attribute, IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;
            var limiter = http.RequestServices.GetRequiredService<SearchRateLimiter>();
            var (acquired, retryAfter) = limiter.TryAcquire(
                SearchRateLimiter.ClientKey(http.User.GetUserId(), http.Connection.RemoteIpAddress?.ToString()));

            if (acquired)
            {
                await next();
                return;
            }

            SansPost.Infrastructure.Hosting.SansPostTelemetry.RateLimitRejections.Add(1, new KeyValuePair<string, object?>("policy", "Search"));
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SansPost.RateLimiting")
                .LogWarning("Rate limit {Policy} rejected {Method} {Path} from {ClientIp}.", "Search", http.Request.Method, http.Request.Path.Value, http.Connection.RemoteIpAddress?.ToString());

            if (retryAfter is { } wait)
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
        }
    }
}
