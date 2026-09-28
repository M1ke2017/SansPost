module SansPost.Game.Core.Tests.Support

open Xunit
open SansPost.Game.Core

let ok result =
    match result with
    | Ok value -> value
    | Error error -> failwithf "Oczekiwano Ok, jest błąd: %A" error

let err result =
    match result with
    | Ok value -> failwithf "Oczekiwano błędu, jest Ok: %A" value
    | Error error -> error

let card =
    function
    | "Shoot" -> Shoot
    | "Dodge" -> Dodge
    | "Reload" -> Reload
    | "Block" -> Block
    | "Taunt" -> Taunt
    | other -> failwithf "Nieznana karta %s" other

/// Stan z zadanymi statystykami obu graczy (reszta — nowy pojedynek).
let withPlayers (one: PlayerState) (two: PlayerState) =
    { Duel.createStandard () with
        PlayerOne = one
        PlayerTwo = two }

let player prestige ammo = { Prestige = prestige; Ammo = ammo }

/// Jedna pełna runda: obie karty, rozstrzygnięcie.
let play oneCard twoCard state =
    state
    |> Duel.submitMove PlayerOne oneCard
    |> Result.bind (Duel.submitMove PlayerTwo twoCard)
    |> Result.bind Duel.resolveRound
    |> ok

let playAll moves state =
    moves |> List.fold (fun s (one, two) -> play one two s |> fst) state

let assertPlayer (expected: PlayerState) (actual: PlayerState) =
    Assert.Equal(expected.Prestige, actual.Prestige)
    Assert.Equal(expected.Ammo, actual.Ammo)
