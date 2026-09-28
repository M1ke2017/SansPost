/// Sprint 20 — koniec czasu rundy i oddanie pojedynku: kary liczy silnik (nie hub, nie UI), bez losowania ruchów.
module SansPost.Game.Core.Tests.TimeoutForfeitTests

open Xunit
open SansPost.Game.Core
open Support

// ---- Koniec czasu rundy ---------------------------------------------------------------------------------------------

[<Fact>]
let ``one player times out: loses 1 prestige, the other card is discarded without effect`` () =
    let state = Duel.createStandard () |> Duel.submitMove PlayerOne Shoot |> ok

    let next, record = Duel.timeoutRound 1 state |> ok

    assertPlayer (player 3 1) next.PlayerOne            // strzał nie padł — nabój nie zużyty
    assertPlayer (player 2 1) next.PlayerTwo
    Assert.Equal<Effect list>([ TimedOut PlayerTwo ], record.Effects)
    Assert.Equal((Some Shoot, None), (record.PlayerOneCard, record.PlayerTwoCard))
    Assert.Equal(2, next.Round)
    Assert.Equal({ One = None; Two = None }, next.Pending)
    Assert.Equal(WaitingForMoves, next.Phase)

[<Fact>]
let ``both players time out: both lose 1 prestige`` () =
    let next, record = Duel.createStandard () |> Duel.timeoutRound 1 |> ok

    assertPlayer (player 2 1) next.PlayerOne
    assertPlayer (player 2 1) next.PlayerTwo
    Assert.Equal<Effect list>([ TimedOut PlayerOne; TimedOut PlayerTwo ], record.Effects)
    Assert.Equal((None, None), (record.PlayerOneCard, record.PlayerTwoCard))

[<Fact>]
let ``double timeout knock-out is a draw`` () =
    let state = withPlayers (player 1 1) (player 1 0)

    let final, _ = Duel.timeoutRound 1 state |> ok

    Assert.Equal(Finished Draw, final.Phase)
    Assert.Equal(None, Rules.winner Draw)

[<Fact>]
let ``timeout knock-out of one player ends the duel`` () =
    let state = withPlayers (player 3 1) (player 1 1) |> Duel.submitMove PlayerOne Reload |> ok

    let final, _ = Duel.timeoutRound 1 state |> ok

    Assert.Equal(Finished PlayerOneWins, final.Phase)
    assertPlayer (player 3 1) final.PlayerOne            // przeładowanie przepadło razem z rundą

[<Fact>]
let ``timeout at the last round decides by prestige`` () =
    let state = { withPlayers (player 2 1) (player 2 1) with Round = 12 } |> Duel.submitMove PlayerTwo Block |> ok

    let final, _ = Duel.timeoutRound 12 state |> ok

    Assert.Equal(Finished PlayerTwoWins, final.Phase)

[<Fact>]
let ``late timer for an old round is stale and changes nothing`` () =
    let state = Duel.createStandard () |> play Dodge Dodge |> fst

    Assert.Equal(StaleRound(Expected = 1, Actual = 2), Duel.timeoutRound 1 state |> err)
    Assert.Equal(DuelAlreadyFinished, withPlayers (player 1 1) (player 1 0) |> play Shoot Reload |> fst |> Duel.timeoutRound 1 |> err)

[<Fact>]
let ``timeout with both cards chosen is a caller error, not a penalty`` () =
    let ready = Duel.createStandard () |> Duel.submitMove PlayerOne Dodge |> Result.bind (Duel.submitMove PlayerTwo Dodge) |> ok

    match Duel.timeoutRound 1 ready |> err with
    | InvalidState _ -> ()
    | other -> failwithf "Oczekiwano InvalidState, jest %A" other

[<Fact>]
let ``timed-out round is in history for both views, hidden card never leaked before`` () =
    let waiting = Duel.createStandard () |> Duel.submitMove PlayerOne Taunt |> ok
    Assert.True((Duel.viewFor PlayerTwo waiting).OpponentReady)
    Assert.Equal(None, (Duel.viewFor PlayerTwo waiting).MyMove)

    let next, _ = Duel.timeoutRound 1 waiting |> ok
    let view = Duel.viewFor PlayerTwo next

    let record = Assert.Single(view.History)
    Assert.Equal(Some Taunt, record.PlayerOneCard)       // po zamknięciu rundy — jak zwykłe odsłonięcie
    Assert.False(view.OpponentReady)
    Assert.Equal<Card list>(Rules.allCards, view.Available)

// ---- Oddanie pojedynku ------------------------------------------------------------------------------------------------

[<Fact>]
let ``forfeit finishes the duel, opponent wins, hidden cards stay hidden`` () =
    let state = Duel.createStandard () |> play Reload Dodge |> fst |> Duel.submitMove PlayerTwo Shoot |> ok

    let final = Duel.forfeit PlayerOne state |> ok

    Assert.Equal(Finished(Forfeited PlayerOne), final.Phase)
    Assert.Equal(Some PlayerTwo, Rules.winner (Forfeited PlayerOne))
    Assert.Equal(Some PlayerOne, Rules.winner (Forfeited PlayerTwo))
    Assert.Equal({ One = None; Two = None }, final.Pending)
    Assert.Equal(1, final.History.Length)                // rozegrana runda zostaje, ukryty strzał nie trafia do historii
    Assert.Equal(state.PlayerOne, final.PlayerOne)       // bez kary prestiżu — wynik to oddanie, nie nokaut
    Assert.Empty(Duel.availableCards PlayerTwo final)

[<Fact>]
let ``no forfeit, timeout or move after the duel is finished`` () =
    let forfeited = Duel.createStandard () |> Duel.forfeit PlayerTwo |> ok

    Assert.Equal(DuelAlreadyFinished, Duel.forfeit PlayerOne forfeited |> err)
    Assert.Equal(DuelAlreadyFinished, Duel.timeoutRound 1 forfeited |> err)
    Assert.Equal(DuelAlreadyFinished, Duel.submitMove PlayerOne Dodge forfeited |> err)
    Assert.Equal(Finished(Forfeited PlayerTwo), forfeited.Phase)

[<Fact>]
let ``game over results map to a single winner`` () =
    Assert.Equal(Some PlayerOne, Rules.winner PlayerOneWins)
    Assert.Equal(Some PlayerTwo, Rules.winner PlayerTwoWins)

[<Fact>]
let ``timeout and forfeit never modify their input`` () =
    let state = Duel.createStandard () |> Duel.submitMove PlayerOne Block |> ok
    let copy = state

    Duel.timeoutRound 1 state |> ok |> ignore
    Duel.forfeit PlayerTwo state |> ok |> ignore

    Assert.Equal(copy, state)
    Assert.Equal(Some Block, state.Pending.One)
