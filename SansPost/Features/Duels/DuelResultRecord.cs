using SansPost.Features.Identity;

namespace SansPost.Features.Duels
{
    // Wynik zakończonego pojedynku gracz kontra gracz (Sprint 21). Trwający GameState zostaje w pamięci serwera —
    // do bazy trafia tylko wynik: kto grał, kto wygrał, ile rund, jak się skończył. Bez kart i bez przebiegu rund.
    // Trening z manekinem nie jest zapisywany. Historia nie jest kasowana (także po zawieszeniu / banie gracza).
    public class DuelResultRecord
    {
        public int Id { get; set; }

        // Identyfikator sesji pojedynku — UNIQUE: wynik zapisuje się dokładnie raz, nawet przy równoległym zakończeniu.
        public Guid DuelId { get; set; }

        public int PlayerOneId { get; set; }
        public User PlayerOne { get; set; } = null!;

        public int PlayerTwoId { get; set; }
        public User PlayerTwo { get; set; } = null!;

        // null = remis.
        public int? WinnerId { get; set; }
        public User? Winner { get; set; }

        public DuelResultType ResultType { get; set; }
        public int RoundCount { get; set; }
        public DateTime FinishedAt { get; set; }
        public DuelFinishReason FinishReason { get; set; }
    }

    public enum DuelResultType
    {
        Win,
        Draw
    }

    public enum DuelFinishReason
    {
        // Nokaut (prestiż spadł do zera — także po karach za brak ruchu).
        Knockout,
        // Limit rund — więcej prestiżu albo remis.
        RoundLimit,
        Surrender,
        // Brak powrotu po zerwaniu połączenia.
        Disconnect
    }
}
