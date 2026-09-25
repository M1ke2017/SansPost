using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Profiles;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Trasa po nazwie użytkownika (unikalna, bez względu na wielkość liter od Sprintu 2) — czytelne, udostępnialne URL-e.
    [Route("api/profiles")]
    public class ProfilesController : ApiControllerBase
    {
        private readonly ProfileService _profileService;

        public ProfilesController(ProfileService profileService)
        {
            _profileService = profileService;
        }

        [AllowAnonymous]
        [HttpGet("{username}")]
        public async Task<IActionResult> Get(string username, CancellationToken cancellationToken)
        {
            var profile = await _profileService.GetByUsernameAsync(username, User.GetUserId(), cancellationToken);
            return profile is null ? NotFound() : Ok(profile);
        }
    }
}
