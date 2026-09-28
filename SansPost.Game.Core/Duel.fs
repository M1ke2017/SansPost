/// Maszyna stanów pojedynku: tworzenie, ukryte zgłaszanie kart, jednoczesne rozstrzygnięcie rundy, koniec gry.
/// Każda funkcja zwraca nowy, niezmienny stan albo GameError — nigdy nie modyfikuje stanu wejściowego.
module SansPost.Game.Core.Duel

let private noMoves = { One = None; Two = None }

let create (rules: DuelRules) =
    let start =
        { Prestige = rules.StartPrestige
          Ammo = rules.StartAmmo }

    { Rules = rules
      Round = 1
      PlayerOne = start
      PlayerTwo = start
      Pending = noMoves
      Phase = WaitingForMoves
      History = [] }

let createStandard () = create Rules.standard

let playerState player (state: GameState) =
    match player with
    | PlayerOne -> state.PlayerOne
    | PlayerTwo -> state.PlayerTwo

let pendingMove player (state: GameState) =
    match player with
    | PlayerOne -> state.Pending.One
    | PlayerTwo -> state.Pending.Two

let isFinished (state: GameState) =
    match state.Phase with
    | Finished _ -> true
    | WaitingForMoves -> false

/// Obie karty rundy są już wybrane.
let isReady (state: GameState) =
    state.Pending.One.IsSome && state.Pending.Two.IsSome

/// Niezmienniki stanu — naruszenie oznacza błąd wywołującego, nie ruch gracza.
let private validate (state: GameState) =
    let valid (p: PlayerState) = p.Ammo >= 0 && p.Ammo <= state.Rules.MaxAmmo

    if state.Round < 1 || state.Round > state.Rules.MaxRounds then
        Error(InvalidState "runda poza zakresem pojedynku")
    elif not (valid state.PlayerOne && valid state.PlayerTwo) then
        Error(InvalidState "amunicja poza zakresem")
    elif isFinished state && (state.Pending.One.IsSome || state.Pending.Two.IsSome) then
        Error(InvalidState "zakończony pojedynek z oczekującym ruchem")
    else
        Ok state

/// Karty, które gracz może zagrać teraz (pusto po zakończeniu albo gdy już wybrał kartę tej rundy).
let availableCards player (state: GameState) =
    if isFinished state || (pendingMove player state).IsSome then []
    else Rules.available (playerState player state)

/// Ukryte zgłoszenie karty. Rozstrzygnięcie dopiero, gdy obie karty są znane (resolveRound).
let submitMove player card (state: GameState) =
    match state.Phase, pendingMove player state with
    | Finished _, _ -> Error DuelAlreadyFinished
    | WaitingForMoves, Some _ -> Error MoveAlreadySubmitted
    | WaitingForMoves, None when not (Rules.canPlay (playerState player state) card) -> Error NotEnoughAmmo
    | WaitingForMoves, None ->
        validate state
        |> Result.map (fun (valid: GameState) ->
            match player with
            | PlayerOne -> { valid with Pending = { valid.Pending with One = Some card } }
            | PlayerTwo -> { valid with Pending = { valid.Pending with Two = Some card } })

/// Zgłoszenie karty z kontrolą rundy — powtórzone lub spóźnione żądanie nie trafia do nowej rundy.
let submitMoveForRound expectedRound player card (state: GameState) =
    if expectedRound <> state.Round && not (isFinished state) then
        Error(StaleRound(Expected = expectedRound, Actual = state.Round))
    else
        submitMove player card state

/// Zamknięcie rundy: zapis w historii, wyczyszczone karty, koniec gry (nokaut / limit rund) albo kolejna runda.
let private closeRound (valid: GameState) (record: RoundRecord) =
    let next =
        { valid with
            PlayerOne = record.PlayerOneAfter
            PlayerTwo = record.PlayerTwoAfter
            Pending = noMoves
            History = valid.History @ [ record ] }

    match Rules.outcome valid.Rules valid.Round record.PlayerOneAfter record.PlayerTwoAfter with
    | Some result -> { next with Phase = Finished result }, record
    | None -> { next with Round = valid.Round + 1 }, record

/// Jednoczesne odsłonięcie i rozstrzygnięcie rundy: nowy stan graczy, zapis w historii, koniec gry albo kolejna runda.
let resolveRound (state: GameState) =
    match state.Phase, state.Pending with
    | Finished _, _ -> Error DuelAlreadyFinished
    | WaitingForMoves, { One = Some oneCard; Two = Some twoCard } ->
        validate state
        |> Result.map (fun (valid: GameState) ->
            let one, two, effects =
                Rules.resolveCards valid.Rules (valid.PlayerOne, oneCard) (valid.PlayerTwo, twoCard)

            closeRound
                valid
                { Round = valid.Round
                  PlayerOneCard = Some oneCard
                  PlayerTwoCard = Some twoCard
                  Effects = effects
                  PlayerOneAfter = one
                  PlayerTwoAfter = two })
    | WaitingForMoves, _ -> Error RoundNotReady

/// Koniec czasu rundy (o czasie decyduje serwer, silnik nie zna zegara): każdy gracz bez karty traci 1 prestiżu,
/// karta tego, kto zdążył, przepada bez efektu. Obaj spóźnieni — obaj tracą; podwójny nokaut to remis (Rules.outcome).
/// Numer rundy chroni przed spóźnionym zegarem: timeout starej rundy nie dotknie nowej.
let timeoutRound expectedRound (state: GameState) =
    match state.Phase with
    | Finished _ -> Error DuelAlreadyFinished
    | WaitingForMoves when expectedRound <> state.Round -> Error(StaleRound(Expected = expectedRound, Actual = state.Round))
    | WaitingForMoves when isReady state -> Error(InvalidState "obie karty wybrane — rundę rozstrzyga resolveRound")
    | WaitingForMoves ->
        validate state
        |> Result.map (fun (valid: GameState) ->
            let settle player (current: PlayerState) =
                match pendingMove player valid with
                | Some _ -> current, []
                | None -> Rules.timeoutPenalty current, [ TimedOut player ]

            let one, oneEffects = settle PlayerOne valid.PlayerOne
            let two, twoEffects = settle PlayerTwo valid.PlayerTwo

            closeRound
                valid
                { Round = valid.Round
                  PlayerOneCard = valid.Pending.One
                  PlayerTwoCard = valid.Pending.Two
                  Effects = oneEffects @ twoEffects
                  PlayerOneAfter = one
                  PlayerTwoAfter = two })

/// Oddanie pojedynku (poddanie albo brak powrotu po rozłączeniu — powód zna serwer). Wygrywa przeciwnik.
/// Ukryte karty bieżącej rundy przepadają nieodsłonięte; rozegrane rundy zostają w historii.
let forfeit player (state: GameState) =
    match state.Phase with
    | Finished _ -> Error DuelAlreadyFinished
    | WaitingForMoves ->
        { state with
            Pending = noMoves
            Phase = Finished(Forfeited player) }
        |> validate

type MoveOutcome =
    /// Karta przyjęta, czekamy na przeciwnika.
    | Waiting of GameState
    /// Druga karta — runda rozstrzygnięta.
    | Resolved of GameState * RoundRecord

/// Zgłoszenie karty i — jeśli to druga karta rundy — natychmiastowe rozstrzygnięcie (jeden krok dla serwera).
let submitAndResolveIfReady expectedRound player card (state: GameState) =
    submitMoveForRound expectedRound player card state
    |> Result.bind (fun submitted ->
        if isReady submitted then
            resolveRound submitted |> Result.map Resolved
        else
            Ok(Waiting submitted))

/// Widok jednego gracza: własna karta rundy tak, karta przeciwnika nigdy — tylko "gotowy / nie".
/// Historia zawiera wyłącznie rozstrzygnięte rundy (karty odsłonięte).
let viewFor player (state: GameState) =
    let opponent = Rules.opponentOf player

    { Me = player
      Round = state.Round
      MaxRounds = state.Rules.MaxRounds
      Mine = playerState player state
      Opponent = playerState opponent state
      MyMove = pendingMove player state
      OpponentReady = (pendingMove opponent state).IsSome
      Phase = state.Phase
      History = state.History
      Available = availableCards player state }
