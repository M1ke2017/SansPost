/// Maszyna stanów pojedynku: walidacja, ukryte ruchy, amunicja, koniec gry, niezmienność i determinizm.
module SansPost.Game.Core.Tests.DuelTests

open Xunit
open SansPost.Game.Core
open Support

[<Fact>]
let ``new duel starts with standard parameters`` () =
    let state = Duel.createStandard ()

    Assert.Equal({ StartPrestige = 3; StartAmmo = 1; MaxAmmo = 2; MaxRounds = 12 }, state.Rules)
    assertPlayer (player 3 1) state.PlayerOne
    assertPlayer (player 3 1) state.PlayerTwo
    Assert.Equal(1, state.Round)
    Assert.Equal(WaitingForMoves, state.Phase)
    Assert.Empty(state.History)
    Assert.False(Duel.isReady state)

// ---- Walidacja ----------------------------------------------------------------------------------------------------

[<Fact>]
let ``shoot without ammo is rejected and state is unchanged`` () =
    let state = withPlayers (player 3 0) (player 3 1)

    Assert.Equal(NotEnoughAmmo, Duel.submitMove PlayerOne Shoot state |> err)
    Assert.DoesNotContain(Shoot, Duel.availableCards PlayerOne state)
    Assert.Contains(Shoot, Duel.availableCards PlayerTwo state)
    Assert.Equal(None, state.Pending.One)

[<Fact>]
let ``second move of the same player in a round is rejected`` () =
    let state = Duel.createStandard () |> Duel.submitMove PlayerOne Dodge |> ok

    Assert.Equal(MoveAlreadySubmitted, Duel.submitMove PlayerOne Block state |> err)
    Assert.Equal(Some Dodge, state.Pending.One)   // pierwszy wybór zostaje
    Assert.Empty(Duel.availableCards PlayerOne state)

[<Fact>]
let ``round cannot be resolved without both moves`` () =
    let empty = Duel.createStandard ()
    let one = empty |> Duel.submitMove PlayerOne Reload |> ok
    let two = empty |> Duel.submitMove PlayerTwo Reload |> ok

    for state in [ empty; one; two ] do
        Assert.Equal(RoundNotReady, Duel.resolveRound state |> err)

[<Fact>]
let ``move for another round is stale`` () =
    let state = Duel.createStandard () |> play Dodge Dodge |> fst

    Assert.Equal(StaleRound(Expected = 1, Actual = 2), Duel.submitMoveForRound 1 PlayerOne Block state |> err)
    Assert.True(Duel.submitMoveForRound 2 PlayerOne Block state |> Result.isOk)

[<Fact>]
let ``no action after the duel is finished`` () =
    let finished = withPlayers (player 1 1) (player 1 0) |> play Shoot Reload |> fst

    Assert.Equal(Finished PlayerOneWins, finished.Phase)
    Assert.Equal(DuelAlreadyFinished, Duel.submitMove PlayerOne Dodge finished |> err)
    Assert.Equal(DuelAlreadyFinished, Duel.submitMove PlayerTwo Dodge finished |> err)
    Assert.Equal(DuelAlreadyFinished, Duel.resolveRound finished |> err)
    Assert.Equal(DuelAlreadyFinished, Duel.submitAndResolveIfReady finished.Round PlayerOne Dodge finished |> err)
    Assert.Empty(Duel.availableCards PlayerOne finished)

[<Fact>]
let ``inconsistent state is reported instead of resolved`` () =
    let corrupt = { withPlayers (player 3 5) (player 3 1) with Pending = { One = Some Dodge; Two = Some Dodge } }

    match Duel.resolveRound corrupt with
    | Error(InvalidState _) -> ()
    | other -> failwithf "Oczekiwano InvalidState, jest %A" other

    let pastLimit = { Duel.createStandard () with Round = 13 }
    match Duel.submitMove PlayerOne Dodge pastLimit with
    | Error(InvalidState _) -> ()
    | other -> failwithf "Oczekiwano InvalidState, jest %A" other

// ---- Amunicja --------------------------------------------------------------------------------------------------------

[<Fact>]
let ``shoot always spends a bullet, even when dodged or blocked`` () =
    for defence in [ Dodge; Block ] do
        let state, _ = withPlayers (player 3 2) (player 3 1) |> play Shoot defence
        Assert.Equal(1, state.PlayerOne.Ammo)
        Assert.Equal(3, state.PlayerTwo.Prestige)

[<Fact>]
let ``reload is capped at max ammo`` () =
    let state, record = withPlayers (player 3 2) (player 3 1) |> play Reload Dodge

    Assert.Equal(2, state.PlayerOne.Ammo)
    Assert.Equal<Effect list>([ ReloadAtMax PlayerOne ], record.Effects)

[<Fact>]
let ``interrupted reload gains nothing`` () =
    let state, _ = withPlayers (player 3 0) (player 3 1) |> play Reload Shoot

    Assert.Equal(0, state.PlayerOne.Ammo)
    Assert.Equal(2, state.PlayerOne.Prestige)

[<Fact>]
let ``reload then shoot uses the new bullet`` () =
    let state = withPlayers (player 3 0) (player 3 0) |> playAll [ Reload, Dodge; Shoot, Reload ]

    Assert.Equal(0, state.PlayerOne.Ammo)
    Assert.Equal(2, state.PlayerTwo.Prestige)
    Assert.Equal(0, state.PlayerTwo.Ammo)   // przeładowanie przerwane

// ---- Koniec pojedynku ------------------------------------------------------------------------------------------------

[<Fact>]
let ``simultaneous shots on the last prestige are a double knockout draw`` () =
    let state, record = withPlayers (player 1 1) (player 1 1) |> play Shoot Shoot

    Assert.Equal(Finished Draw, state.Phase)
    Assert.Equal(0, record.PlayerOneAfter.Prestige)
    Assert.Equal(0, record.PlayerTwoAfter.Prestige)

[<Fact>]
let ``knocking out the opponent wins`` () =
    let p1Wins = withPlayers (player 2 1) (player 1 0) |> play Taunt Block |> fst
    let p2Wins = withPlayers (player 1 1) (player 3 1) |> play Reload Shoot |> fst

    Assert.Equal(Finished PlayerOneWins, p1Wins.Phase)
    Assert.Equal(Finished PlayerTwoWins, p2Wins.Phase)

[<Fact>]
let ``a full duel played to a knockout`` () =
    let state =
        Duel.createStandard ()
        |> playAll [ Shoot, Reload     // P2: 2 prestiżu, przeładowanie przerwane
                     Reload, Taunt     // P1: 1 nabój; prowokacja bez efektu
                     Shoot, Dodge      // pudło
                     Taunt, Block      // P2: 1 prestiżu
                     Reload, Reload    // obaj ładują
                     Shoot, Taunt ]    // P2 trafiony — koniec

    Assert.Equal(Finished PlayerOneWins, state.Phase)
    Assert.Equal(6, state.History.Length)
    Assert.Equal(6, state.Round)                      // numer ostatniej rozegranej rundy
    assertPlayer (player 3 0) state.PlayerOne
    assertPlayer (player 0 2) state.PlayerTwo

[<Fact>]
let ``after max rounds higher prestige wins, equal is a draw`` () =
    let twelveQuietRounds = List.replicate 12 (Dodge, Dodge)

    let draw = Duel.createStandard () |> playAll twelveQuietRounds
    Assert.Equal(Finished Draw, draw.Phase)
    Assert.Equal(12, draw.History.Length)
    Assert.Equal(DuelAlreadyFinished, Duel.submitMove PlayerOne Dodge draw |> err)

    let lead = Duel.createStandard () |> playAll ((Taunt, Dodge) :: List.replicate 11 (Dodge, Dodge))
    Assert.Equal(Finished PlayerOneWins, lead.Phase)
    Assert.Equal(12, lead.History.Length)

    let almost = Duel.createStandard () |> playAll (List.replicate 11 (Dodge, Dodge))
    Assert.Equal(WaitingForMoves, almost.Phase)
    Assert.Equal(12, almost.Round)

// ---- Ukryte ruchy ------------------------------------------------------------------------------------------------

[<Fact>]
let ``opponent sees only readiness, never the hidden card`` () =
    let state = Duel.createStandard () |> Duel.submitMove PlayerOne Taunt |> ok

    let mine = Duel.viewFor PlayerOne state
    let theirs = Duel.viewFor PlayerTwo state

    Assert.Equal(Some Taunt, mine.MyMove)
    Assert.False(mine.OpponentReady)
    Assert.Equal(None, theirs.MyMove)
    Assert.True(theirs.OpponentReady)
    Assert.Empty(theirs.History)                       // runda nierozstrzygnięta — nic nie jest odsłonięte
    Assert.Equal<Card list>(Rules.allCards, theirs.Available)

    // Typ widoku nie ma żadnego pola z kartą przeciwnika — jedyne pole karty to własny ruch.
    let cardFields =
        typeof<DuelView>.GetProperties()
        |> Array.filter (fun p -> p.PropertyType = typeof<Card option>)
        |> Array.map (fun p -> p.Name)
    Assert.Equal<string[]>([| "MyMove" |], cardFields)

[<Fact>]
let ``cards are revealed in history only after resolution`` () =
    let state, _ = Duel.createStandard () |> play Block Taunt
    let view = Duel.viewFor PlayerTwo state

    let revealed = Assert.Single(view.History)
    Assert.Equal(Some Block, revealed.PlayerOneCard)
    Assert.Equal(Some Taunt, revealed.PlayerTwoCard)
    Assert.Equal(1, revealed.Round)
    Assert.Equal(None, view.MyMove)
    Assert.False(view.OpponentReady)

// ---- Czysta funkcja: niezmienność i determinizm ---------------------------------------------------------------------

[<Fact>]
let ``inputs are never modified`` () =
    let start = Duel.createStandard ()
    let snapshot = start
    let submitted = start |> Duel.submitMove PlayerOne Shoot |> ok
    let submittedSnapshot = submitted
    let both = submitted |> Duel.submitMove PlayerTwo Reload |> ok
    let _ = Duel.resolveRound both |> ok

    Assert.Equal(snapshot, start)
    Assert.Equal(None, start.Pending.One)
    Assert.Equal(submittedSnapshot, submitted)
    Assert.Equal(None, submitted.Pending.Two)
    Assert.Equal({ One = Some Shoot; Two = Some Reload }, both.Pending)

[<Fact>]
let ``same moves always give the same duel`` () =
    let moves = [ Reload, Reload; Shoot, Block; Taunt, Dodge; Shoot, Shoot; Reload, Taunt ]
    let a = Duel.createStandard () |> playAll moves
    let b = Duel.createStandard () |> playAll moves

    Assert.Equal(a, b)

[<Fact>]
let ``submit and resolve if ready waits for the second card`` () =
    let start = Duel.createStandard ()

    match Duel.submitAndResolveIfReady 1 PlayerTwo Reload start |> ok with
    | Duel.Waiting waiting ->
        Assert.True(waiting.Pending.Two.IsSome)
        match Duel.submitAndResolveIfReady 1 PlayerOne Shoot waiting |> ok with
        | Duel.Resolved(state, record) ->
            Assert.Equal(2, state.Round)
            Assert.Equal(1, record.Round)
            Assert.Equal({ One = None; Two = None }, state.Pending)
        | other -> failwithf "Oczekiwano Resolved, jest %A" other
    | other -> failwithf "Oczekiwano Waiting, jest %A" other

// ---- Manekin treningowy -------------------------------------------------------------------------------------------

/// Pojedynek gracza zawsze prowokującego z manekinem; karty manekina wybierane z jego własnego widoku.
let private dummyDuel () =
    let rec loop state =
        if Duel.isFinished state then state
        else
            let before = TrainingDummy.chooseCard (Duel.viewFor PlayerTwo state)
            let afterPlayerMove = state |> Duel.submitMove PlayerOne Taunt |> ok
            let after = TrainingDummy.chooseCard (Duel.viewFor PlayerTwo afterPlayerMove)
            Assert.Equal(before, after)                // wybór nie zależy od ukrytej karty gracza
            Assert.True(Rules.canPlay state.PlayerTwo after)
            afterPlayerMove |> Duel.submitMove PlayerTwo after |> Result.bind Duel.resolveRound |> ok |> fst |> loop

    loop (Duel.createStandard ())

[<Fact>]
let ``training dummy is deterministic, legal and blind to the hidden card`` () =
    let first = dummyDuel ()
    let second = dummyDuel ()

    Assert.Equal(first, second)
    Assert.True(Duel.isFinished first)
    Assert.Equal(Some Reload, first.History.Head.PlayerTwoCard)   // scenariusz zaczyna od przeładowania
