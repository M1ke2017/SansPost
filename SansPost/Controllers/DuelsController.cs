using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Duels;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Pojedynki "Śladem Rewolwerowca" (Sprint 19) — fundament przed SignalR: tworzenie, dołączenie, podgląd i ruch.
    // Wymaga JWT (ApiControllerBase). Tożsamość gracza wyłącznie z tokena — nie da się zagrać za przeciwnika.
    // Polityka konta (zawieszone nie grają) — w GameSessionService, nie w kontrolerze; odczyt stanu zostaje dostępny.
    // Rozstrzyga serwer (silnik F#); klient nie liczy wyniku i nie widzi ukrytej karty przeciwnika.
    [Route("api/duels")]
    public class DuelsController : ApiControllerBase
    {
        private readonly GameSessionService _sessions;

        public DuelsController(GameSessionService sessions)
        {
            _sessions = sessions;
        }

        private int UserId => User.GetUserId()!.Value;
        private string Alias => User.Identity?.Name ?? "Rewolwerowiec";

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateDuelRequest? request, CancellationToken cancellationToken)
        {
            var mode = string.Equals(request?.Mode, "training", StringComparison.OrdinalIgnoreCase) ? DuelMode.Training
                : request?.Mode is null or "" || string.Equals(request.Mode, "challenge", StringComparison.OrdinalIgnoreCase) ? DuelMode.Challenge
                : (DuelMode?)null;
            if (mode is null)
                return Problem(detail: "Tryb pojedynku: \"challenge\" albo \"training\".", statusCode: StatusCodes.Status400BadRequest);

            var result = await _sessions.CreateAsync(UserId, Alias, mode.Value, cancellationToken);
            return result.Succeeded
                ? CreatedAtAction(nameof(Get), new { id = result.Value!.DuelId }, result.Value)
                : this.ToProblem(result);
        }

        // Sprint 21: ranking stołu (Top 10) i Mistrz Stołu — tylko przydomki i liczby (bez UserId i e-maili).
        [HttpGet("leaderboard")]
        public async Task<IActionResult> Leaderboard([FromServices] DuelStandingsService standings, CancellationToken cancellationToken) =>
            Ok(await standings.GetStandingsAsync(cancellationToken));

        // Własne statystyki pojedynków (gwiazdki = zwycięstwa PvP).
        [HttpGet("stats")]
        public async Task<IActionResult> Stats([FromServices] DuelStandingsService standings, CancellationToken cancellationToken) =>
            Ok(await standings.GetStatsAsync(UserId, cancellationToken));

        [HttpGet("active")]
        public IActionResult Active()
        {
            var result = _sessions.GetActive(UserId);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpGet("{id:guid}")]
        public IActionResult Get(Guid id)
        {
            var result = _sessions.Get(id, UserId);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpPost("{id:guid}/join")]
        public async Task<IActionResult> Join(Guid id, CancellationToken cancellationToken)
        {
            var result = await _sessions.JoinAsync(id, UserId, Alias, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpPost("{id:guid}/moves")]
        public async Task<IActionResult> Move(Guid id, [FromBody] SubmitMoveRequest request, CancellationToken cancellationToken)
        {
            var result = await _sessions.SubmitMoveAsync(id, UserId, request.Card, request.Round, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }
    }
}
