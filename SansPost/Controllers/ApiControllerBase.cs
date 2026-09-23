using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Bazowa klasa REST API: domyślnie wymaga JWT (secure by default).
    // Publiczne akcje oznaczone [AllowAnonymous] — tożsamość nadal pochodzi wyłącznie z JWT, nigdy z cookie UI.
    [ApiController]
    [Authorize(Policy = AuthPolicies.ApiUser)]
    public abstract class ApiControllerBase : ControllerBase
    {
    }
}
