namespace SansPost.Features.Duels
{
    // Czasy pojedynku na żywo (sekcja "Duels"). O czasie decyduje wyłącznie serwer — klient tylko rysuje odliczanie.
    public sealed class DuelOptions
    {
        public const string Section = "Duels";

        // Ważność wyzwania, zanim wygaśnie bez odpowiedzi.
        public int ChallengeSeconds { get; set; } = 30;

        // Czas na wybór karty w rundzie; po nim silnik F# stosuje karę za brak ruchu (Duel.timeoutRound).
        public int RoundSeconds { get; set; } = 15;

        // Okno powrotu po zerwaniu połączenia; po nim gracz oddaje pojedynek (Duel.forfeit).
        public int GraceSeconds { get; set; } = 20;

        public TimeSpan ChallengeLifetime => TimeSpan.FromSeconds(ChallengeSeconds);
        public TimeSpan RoundDuration => TimeSpan.FromSeconds(RoundSeconds);
        public TimeSpan DisconnectGrace => TimeSpan.FromSeconds(GraceSeconds);

        public bool IsValid => ChallengeSeconds is >= 1 and <= 300 && RoundSeconds is >= 1 and <= 300 && GraceSeconds is >= 1 and <= 300;
    }
}
