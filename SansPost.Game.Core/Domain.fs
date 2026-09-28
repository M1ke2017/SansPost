/// Model pojedynku "Śladem Rewolwerowca": niezmienne rekordy i unie. Żadnej infrastruktury, DTO ani losowości.
namespace SansPost.Game.Core

/// Karta komendy zagrywana w ukryciu, odsłaniana jednocześnie z kartą przeciwnika.
type Card =
    | Shoot
    | Dodge
    | Reload
    | Block
    | Taunt

type Player =
    | PlayerOne
    | PlayerTwo

type PlayerState =
    { Prestige: int
      Ammo: int }

type DuelResult =
    | PlayerOneWins
    | PlayerTwoWins
    | Draw
    /// Pojedynek oddany przez wskazanego gracza (poddanie albo brak powrotu po zerwaniu połączenia) — wygrywa przeciwnik.
    | Forfeited of Player

type DuelPhase =
    | WaitingForMoves
    | Finished of DuelResult

/// Parametry pojedynku (domyślne: Rules.standard). Bez losowości — ten sam przebieg dla tych samych kart.
type DuelRules =
    { StartPrestige: int
      StartAmmo: int
      MaxAmmo: int
      MaxRounds: int }

/// Co wydarzyło się w rundzie — opis dla historii i UI (tłumaczenie na tekst robi warstwa C#).
type Effect =
    /// Strzał trafił; cel traci 1 prestiżu.
    | ShotHit of Shooter: Player
    | ShotDodged of Shooter: Player
    | ShotBlocked of Shooter: Player
    /// Przeładowanie udane: +1 nabój.
    | Reloaded of Player
    /// Przeładowanie przy pełnym bębenku — bez zmiany.
    | ReloadAtMax of Player
    /// Przeładowanie przerwane trafieniem.
    | ReloadInterrupted of Player
    /// Prowokacja ukarała unik albo blok przeciwnika (traci 1 prestiżu).
    | TauntPunished of Taunter: Player
    /// Gracz nie wybrał karty przed końcem czasu rundy — traci 1 prestiżu (bez losowania ruchu za niego).
    | TimedOut of Player

/// Oczekujące, ukryte zagrania bieżącej rundy.
type PendingMoves =
    { One: Card option
      Two: Card option }

/// Rozegrana runda — karty odsłonięte dopiero po rozstrzygnięciu. None = gracz nie wybrał karty przed końcem czasu.
type RoundRecord =
    { Round: int
      PlayerOneCard: Card option
      PlayerTwoCard: Card option
      Effects: Effect list
      PlayerOneAfter: PlayerState
      PlayerTwoAfter: PlayerState }

/// Pełny stan pojedynku. Trzyma ukryte karty — klient dostaje wyłącznie DuelView (Duel.viewFor).
type GameState =
    { Rules: DuelRules
      Round: int
      PlayerOne: PlayerState
      PlayerTwo: PlayerState
      Pending: PendingMoves
      Phase: DuelPhase
      /// Rozegrane rundy, od najstarszej.
      History: RoundRecord list }

/// Zwykłe błędy domeny — zwracane jako Result, nie wyjątki.
type GameError =
    | NotEnoughAmmo
    | MoveAlreadySubmitted
    | DuelAlreadyFinished
    | RoundNotReady
    /// Ruch dotyczy innej rundy niż bieżąca (np. powtórzone żądanie po rozstrzygnięciu).
    | StaleRound of Expected: int * Actual: int
    | InvalidState of Reason: string

/// Stan widziany przez jednego gracza: własna karta tej rundy (jeśli wybrana), o przeciwniku tylko "gotowy / nie".
type DuelView =
    { Me: Player
      Round: int
      MaxRounds: int
      Mine: PlayerState
      Opponent: PlayerState
      MyMove: Card option
      OpponentReady: bool
      Phase: DuelPhase
      History: RoundRecord list
      Available: Card list }
