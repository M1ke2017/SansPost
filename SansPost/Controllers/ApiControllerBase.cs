using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Bazowa klasa REST API: domyślnie wymaga JWT (secure by default).
    // Publiczne akcje oznaczone [AllowAnonymous] — tożsamość nadal pochodzi wyłącznie z JWT, nigdy z cookie UI.
    [ApiController]
    [Authorize(Policy = AuthPolicies.ApiUser)]
    public abstract class ApiControllerBase : ControllerBase
    {
        // Optimistic concurrency przez standardowe HTTP: GET → ETag, PUT/DELETE → wymagany If-Match.
        protected void SetETag(int version) => Response.Headers.ETag = ETags.Format(version);

        // null = poprawny If-Match (version ustawione); w przeciwnym razie gotowa odpowiedź błędu 428/400.
        protected IActionResult? RequireIfMatch(out int version)
        {
            version = 0;

            var header = Request.Headers.IfMatch.ToString();
            if (string.IsNullOrEmpty(header))
            {
                return Problem(
                    detail: "Wymagany nagłówek If-Match z ETag pobranym przez GET.",
                    statusCode: StatusCodes.Status428PreconditionRequired);
            }

            if (!ETags.TryParse(header, out version))
            {
                return Problem(
                    detail: "Nieprawidłowy nagłówek If-Match. Oczekiwany pojedynczy ETag, np. \"3\".",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return null;
        }
    }

    public static class ETags
    {
        // Silny ETag = wersja zasobu, np. "3".
        public static string Format(int version) => new EntityTagHeaderValue($"\"{version}\"").ToString();

        public static bool TryParse(string header, out int version)
        {
            version = 0;
            if (!EntityTagHeaderValue.TryParse(header, out var tag) || tag.IsWeak || tag == EntityTagHeaderValue.Any)
                return false;

            return int.TryParse(tag.Tag.AsSpan().Trim('"'), out version) && version > 0;
        }
    }
}
