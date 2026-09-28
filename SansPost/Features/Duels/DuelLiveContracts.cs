namespace SansPost.Features.Duels
{
    // Kontrakty stołu gry na żywo (DuelHub, Sprint 20). Bez UserId i e-maili — gracz przy stole to alias i nieprzezroczysty
    // uchwyt (Handle) ważny, dopóki jest online. Tożsamość wywołującego zawsze z uwierzytelnionego połączenia, nigdy z danych.

    public sealed record OnlinePlayer(string Handle, string Alias);

    // Direction: "incoming" | "outgoing". Status: "pending" | "accepted" | "rejected" | "cancelled" | "expired" | "unavailable".
    public sealed record ChallengeInfo(string ChallengeId, string Direction, string OpponentAlias, DateTimeOffset ExpiresAt, int RemainingMs, string Status);

    // DuelChallengeReceived — minimum dla wyzwanego.
    public sealed record ChallengeReceived(string ChallengeId, string ChallengerAlias, DateTimeOffset ExpiresAt, int RemainingMs);

    // Stan przy wejściu i po każdym ponownym połączeniu (RequestState): gracze, wyzwania, trwający pojedynek (wznowienie).
    public sealed record LobbyState(string YourAlias, IReadOnlyList<OnlinePlayer> Players, IReadOnlyList<ChallengeInfo> Challenges, DuelSnapshot? Duel);

    public sealed record RoundStartedEvent(string DuelId, int Round, DateTimeOffset DeadlineUtc, int RemainingMs, DuelSnapshot Snapshot);

    // Po rozstrzygnięciu (obie karty albo koniec czasu): obie karty, efekty, prestiż, amunicja; GameOver = wynik, jeśli koniec.
    public sealed record RoundResolvedEvent(string DuelId, int Round, RoundResult Result, string? GameOver, DuelSnapshot Snapshot);

    // Odpowiedź metod huba — ten sam kontrakt błędów co REST (kod + bezpieczny komunikat), bez wyjątków i szczegółów serwera.
    public sealed record HubResult<T>(bool Succeeded, string? Code, string? Message, T? Value)
    {
        public static HubResult<T> From(ServiceResult<T> result) =>
            new(result.Succeeded, result.Code, result.Succeeded ? null : result.Message, result.Value);
    }

    public static class DuelEvents
    {
        public const string ChallengeReceived = "DuelChallengeReceived";
        public const string ChallengeUpdated = "DuelChallengeUpdated";
        public const string DuelUpdated = "DuelUpdated";
        public const string RoundStarted = "RoundStarted";
        public const string RoundResolved = "RoundResolved";
        public const string PresenceChanged = "PresenceChanged";
    }

    // Zdarzenie do wysłania po zmianie stanu (zbierane pod blokadą, wysyłane po jej zwolnieniu). UserId null = wszyscy przy stole.
    public sealed record DuelEvent(int? UserId, string Name, object? Payload)
    {
        public static DuelEvent Presence { get; } = new(null, DuelEvents.PresenceChanged, null);
    }

    public interface IDuelNotifier
    {
        Task PublishAsync(IReadOnlyList<DuelEvent> events);
    }
}
