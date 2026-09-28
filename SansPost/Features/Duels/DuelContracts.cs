using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Duels
{
    // Pojedynek "Śladem Rewolwerowca" z perspektywy jednego gracza ("Ty" / "Przeciwnik"). Bez UserId i bez ukrytej karty
    // przeciwnika — o jego bieżącej rundzie wiadomo tylko, czy jest gotowy. Karty odsłania historia rozegranych rund.
    // Sprint 20 (pojedynek na żywo): Version rośnie z każdą zmianą sesji — klient odrzuca starszy stan, który dotarł
    // po nowszym. Czas rundy liczy serwer: RoundDeadline + RoundRemainingMs (klient rysuje odliczanie od chwili odbioru).
    public sealed record DuelSnapshot(
        string DuelId,
        string Mode,              // "training" | "challenge" (REST) | "live" (wyzwanie przez stół gry, SignalR)
        string Status,            // "waiting-for-opponent" | "ready-check" | "in-progress" | "finished"
        int Round,
        int MaxRounds,
        PlayerSnapshot You,
        PlayerSnapshot? Opponent,
        string? YourMove,         // Twoja karta tej rundy (tylko Ty ją widzisz)
        bool OpponentReady,
        string? Result,           // "win" | "loss" | "draw" po zakończeniu
        IReadOnlyList<AvailableAction> AvailableActions,
        IReadOnlyList<RoundResult> History,
        long Version = 0,
        string? Ending = null,                // "surrender" | "disconnect" — pojedynek oddany; inaczej null
        DateTimeOffset? RoundDeadline = null,
        int? RoundRemainingMs = null,
        string Rematch = "none",              // "none" | "you" (czekasz na zgodę) | "opponent" (przeciwnik proponuje) | "started"
        string? RematchDuelId = null,
        bool OpponentLeft = false);

    // Ready = karta tej rundy wybrana (bez ujawniania jakiej). StartReady = "GOTOWY" przed pierwszą rundą.
    // Online = false, gdy gracz stracił połączenie; GraceRemainingMs — ile zostało do oddania pojedynku.
    public sealed record PlayerSnapshot(string Alias, int Prestige, int Ammo, bool Ready, bool StartReady = false, bool Online = true, int? GraceRemainingMs = null);

    // Rozegrana runda: obie karty (już odsłonięte; null = brak ruchu przed końcem czasu), efekty po polsku, stan po rundzie.
    public sealed record RoundResult(
        int Round,
        string? YourCard,
        string? OpponentCard,
        IReadOnlyList<string> Effects,
        int YourPrestige,
        int YourAmmo,
        int OpponentPrestige,
        int OpponentAmmo);

    public sealed record AvailableAction(string Card, string Name, string Description, bool Enabled, string? DisabledReason);

    // Odpowiedź na ruch: Resolved = to była druga karta rundy (serwer rozstrzygnął ją silnikiem F#).
    public sealed record MoveResponse(bool Accepted, bool Resolved, bool OpponentReady, RoundResult? RoundResult, DuelSnapshot Snapshot);

    public sealed class CreateDuelRequest
    {
        // "challenge" (domyślnie) — czeka na drugiego gracza; "training" — pojedynek z manekinem.
        public string? Mode { get; set; }
    }

    public sealed class SubmitMoveRequest
    {
        [Required(ErrorMessage = "Karta jest wymagana.")]
        public string? Card { get; set; }

        // Runda, której dotyczy ruch — powtórzone albo spóźnione żądanie nie trafi do kolejnej rundy.
        [Range(1, 100, ErrorMessage = "Nieprawidłowy numer rundy.")]
        public int Round { get; set; }
    }

    public enum DuelMode
    {
        Challenge,
        Training,

        // Sprint 20: wyzwanie przyjęte przy stole gry — faza gotowości, zegar rundy, rozłączenia i rewanż (SignalR).
        Live
    }
}
