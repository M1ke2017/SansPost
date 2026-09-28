using SansPost.Game.Core;

namespace SansPost.Features.Duels
{
    // Most F# → C#: typy silnika (unie, rekordy, Result) na DTO dla HTTP i Blazora. Zero logiki gry — reguły,
    // walidacja i rozstrzyganie są wyłącznie w SansPost.Game.Core. Widok gracza (Duel.viewFor) już nie zawiera
    // ukrytej karty przeciwnika, więc tu nie ma jak jej ujawnić.
    public static class DuelMapper
    {
        public const string TrainingAlias = "Manekin treningowy";

        private static readonly (Card Card, string Id, string Name, string Description)[] Cards =
        {
            (Card.Shoot, "shoot", "Strzał", "Zużywa 1 nabój. Trafia przeładowanie, prowokację i strzał."),
            (Card.Dodge, "dodge", "Unik", "Unika strzału, ale prowokacja go karze."),
            (Card.Reload, "reload", "Przeładowanie", "+1 nabój (maks. 2), jeśli przeciwnik Cię nie trafi."),
            (Card.Block, "block", "Blok", "Zatrzymuje strzał, ale prowokacja go karze."),
            (Card.Taunt, "taunt", "Prowokacja", "Karze unik i blok przeciwnika. Wystawia na strzał.")
        };

        public static IReadOnlyList<(string Id, string Name, string Description)> Catalog { get; } =
            Cards.Select(c => (c.Id, c.Name, c.Description)).ToList();

        public static string CardId(Card card) => Cards.First(c => c.Card.Equals(card)).Id;

        public static string CardName(string id) => Cards.FirstOrDefault(c => c.Id == id).Name ?? id;

        public static Card? ParseCard(string? id) =>
            Cards.FirstOrDefault(c => string.Equals(c.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)).Card;

        public static DuelSnapshot Snapshot(string duelId, DuelMode mode, bool opponentJoined, string yourAlias, string? opponentAlias, DuelView view)
        {
            var finished = view.Phase.IsFinished;
            var status = finished ? "finished" : opponentJoined ? "in-progress" : "waiting-for-opponent";
            var you = new PlayerSnapshot(yourAlias, view.Mine.Prestige, view.Mine.Ammo, view.MyMove is not null);
            var opponent = opponentJoined
                ? new PlayerSnapshot(opponentAlias ?? TrainingAlias, view.Opponent.Prestige, view.Opponent.Ammo, view.OpponentReady)
                : null;

            return new DuelSnapshot(
                duelId,
                mode == DuelMode.Training ? "training" : "challenge",
                status,
                view.Round,
                view.MaxRounds,
                you,
                opponent,
                view.MyMove is { } move ? CardId(move.Value) : null,
                view.OpponentReady,
                finished ? ResultFor(view.Me, ((DuelPhase.Finished)view.Phase).Item) : null,
                Actions(view, opponentJoined),
                view.History.Select(record => Round(view.Me, record)).ToList());
        }

        // Karty do wyboru: wszystkie pięć, z powodem, gdy karta jest teraz niedostępna.
        private static IReadOnlyList<AvailableAction> Actions(DuelView view, bool opponentJoined)
        {
            var available = view.Available.ToHashSet();
            return Cards.Select(c =>
            {
                string? reason = null;
                if (view.Phase.IsFinished)
                    reason = "Pojedynek zakończony.";
                else if (!opponentJoined)
                    reason = "Czekamy na przeciwnika.";
                else if (view.MyMove is not null)
                    reason = "Karta na tę rundę już wybrana.";
                else if (!available.Contains(c.Card))
                    reason = "Brak naboju.";
                return new AvailableAction(c.Id, c.Name, c.Description, reason is null, reason);
            }).ToList();
        }

        public static RoundResult Round(Player me, RoundRecord record)
        {
            var mineFirst = me.IsPlayerOne;
            var (mine, theirs) = mineFirst ? (record.PlayerOneAfter, record.PlayerTwoAfter) : (record.PlayerTwoAfter, record.PlayerOneAfter);
            return new RoundResult(
                record.Round,
                CardId(mineFirst ? record.PlayerOneCard : record.PlayerTwoCard),
                CardId(mineFirst ? record.PlayerTwoCard : record.PlayerOneCard),
                record.Effects.Select(effect => Describe(me, effect)).ToList(),
                mine.Prestige, mine.Ammo, theirs.Prestige, theirs.Ammo);
        }

        private static string ResultFor(Player me, DuelResult result) =>
            result.IsDraw ? "draw" : (result.IsPlayerOneWins == me.IsPlayerOne) ? "win" : "loss";

        // Efekty rundy po polsku, z perspektywy oglądającego (bez form rodzajowych).
        public static string Describe(Player me, Effect effect) => effect switch
        {
            Effect.ShotHit hit => hit.Shooter.Equals(me) ? "Twój strzał trafił." : "Strzał przeciwnika trafił Cię.",
            Effect.ShotDodged dodged => dodged.Shooter.Equals(me) ? "Twój strzał chybił — przeciwnik zrobił unik." : "Unik udany — strzał przeciwnika chybił.",
            Effect.ShotBlocked blocked => blocked.Shooter.Equals(me) ? "Blok przeciwnika zatrzymał Twój strzał." : "Twój blok zatrzymał strzał.",
            Effect.Reloaded reloaded => reloaded.Item.Equals(me) ? "Przeładowanie: +1 nabój." : "Przeciwnik przeładował broń.",
            Effect.ReloadAtMax full => full.Item.Equals(me) ? "Bęben pełny — przeładowanie bez zmian." : "Przeciwnik przeładował pełny bęben.",
            Effect.ReloadInterrupted interrupted => interrupted.Item.Equals(me) ? "Trafienie przerwało Twoje przeładowanie." : "Twój strzał przerwał przeładowanie przeciwnika.",
            Effect.TauntPunished taunt => taunt.Taunter.Equals(me) ? "Twoja prowokacja ukarała defensywę przeciwnika." : "Prowokacja przeciwnika ukarała Twoją defensywę.",
            _ => "Nieznany efekt."
        };

        // Błędy domeny (Result silnika) na wynik use-case. InvalidState to błąd serwera, nie gracza.
        public static (ServiceError Error, string Message, string Code) Error(GameError error) => error switch
        {
            { IsNotEnoughAmmo: true } => (ServiceError.Validation, "Brak naboju — strzał jest niemożliwy.", "duel-no-ammo"),
            { IsMoveAlreadySubmitted: true } => (ServiceError.Conflict, "Karta na tę rundę jest już wybrana.", "duel-move-already-submitted"),
            { IsDuelAlreadyFinished: true } => (ServiceError.Conflict, "Pojedynek jest już zakończony.", "duel-finished"),
            GameError.StaleRound stale => (ServiceError.Conflict, $"Ruch dotyczy rundy {stale.Expected}, a trwa runda {stale.Actual}.", "duel-stale-round"),
            { IsRoundNotReady: true } => (ServiceError.Conflict, "Runda nie jest gotowa do rozstrzygnięcia.", "duel-round-not-ready"),
            _ => (ServiceError.Conflict, "Nieprawidłowy stan pojedynku.", "duel-invalid-state")
        };
    }
}
