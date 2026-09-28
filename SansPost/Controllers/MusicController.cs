using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Music;

namespace SansPost.Controllers
{
    // Kącik muzyczny: lista stacji country (discovery). Tylko metadane i adres strumienia — audio pobiera przeglądarka
    // prosto ze stacji. Radio niedostępne (brak świeżej i ostatniej poprawnej listy) → 503 z krótkim opisem.
    [Route("api/music")]
    public class MusicController : ApiControllerBase
    {
        private readonly MusicService _music;

        public MusicController(MusicService music)
        {
            _music = music;
        }

        [AllowAnonymous]
        [HttpGet("stations")]
        public async Task<IActionResult> Stations(CancellationToken cancellationToken)
        {
            var result = await _music.GetStationsAsync(cancellationToken);
            return result.Available
                ? Ok(new RadioStationsResponse(result.Stations))
                : Problem(detail: "Radio jest chwilowo niedostępne.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
