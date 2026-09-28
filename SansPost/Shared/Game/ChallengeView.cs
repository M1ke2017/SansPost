namespace SansPost.Shared.Game
{
    // Wyzwanie w lobby stołu: dane z huba + lokalny termin wygaśnięcia (liczony w przeglądarce przez duel.js).
    public sealed record ChallengeView(string ChallengeId, string Direction, string OpponentAlias, string Status, double LocalDeadline);
}
