/// Pełna macierz 5 × 5 kart: start rundy — obaj gracze Prestige 3, Ammo 1 (strzał legalny).
module SansPost.Game.Core.Tests.RulesMatrixTests

open Xunit
open SansPost.Game.Core
open Support

[<Theory>]
//          karta 1   karta 2   P1 prestiż  P2 prestiż  P1 naboje  P2 naboje
[<InlineData("Shoot",  "Shoot",  2, 2, 0, 0)>]   // obaj trafieni
[<InlineData("Shoot",  "Dodge",  3, 3, 0, 1)>]   // pudło, nabój zużyty
[<InlineData("Shoot",  "Reload", 3, 2, 0, 1)>]   // przeładowanie przerwane i trafione
[<InlineData("Shoot",  "Block",  3, 3, 0, 1)>]   // zablokowany
[<InlineData("Shoot",  "Taunt",  3, 2, 0, 1)>]   // prowokujący trafiony
[<InlineData("Dodge",  "Shoot",  3, 3, 1, 0)>]
[<InlineData("Dodge",  "Dodge",  3, 3, 1, 1)>]
[<InlineData("Dodge",  "Reload", 3, 3, 1, 2)>]
[<InlineData("Dodge",  "Block",  3, 3, 1, 1)>]
[<InlineData("Dodge",  "Taunt",  2, 3, 1, 1)>]   // prowokacja karze unik
[<InlineData("Reload", "Shoot",  2, 3, 1, 0)>]
[<InlineData("Reload", "Dodge",  3, 3, 2, 1)>]
[<InlineData("Reload", "Reload", 3, 3, 2, 2)>]
[<InlineData("Reload", "Block",  3, 3, 2, 1)>]
[<InlineData("Reload", "Taunt",  3, 3, 2, 1)>]   // prowokacja nie przeszkadza w przeładowaniu
[<InlineData("Block",  "Shoot",  3, 3, 1, 0)>]
[<InlineData("Block",  "Dodge",  3, 3, 1, 1)>]
[<InlineData("Block",  "Reload", 3, 3, 1, 2)>]
[<InlineData("Block",  "Block",  3, 3, 1, 1)>]
[<InlineData("Block",  "Taunt",  2, 3, 1, 1)>]   // prowokacja karze blok
[<InlineData("Taunt",  "Shoot",  2, 3, 1, 0)>]
[<InlineData("Taunt",  "Dodge",  3, 2, 1, 1)>]
[<InlineData("Taunt",  "Reload", 3, 3, 1, 2)>]
[<InlineData("Taunt",  "Block",  3, 2, 1, 1)>]
[<InlineData("Taunt",  "Taunt",  3, 3, 1, 1)>]
let ``every pair of cards resolves by the rules`` (one: string) (two: string) p1Prestige p2Prestige p1Ammo p2Ammo =
    let state, record = Duel.createStandard () |> play (card one) (card two)

    assertPlayer (player p1Prestige p1Ammo) state.PlayerOne
    assertPlayer (player p2Prestige p2Ammo) state.PlayerTwo
    Assert.Equal(record.PlayerOneAfter, state.PlayerOne)
    Assert.Equal(record.PlayerTwoAfter, state.PlayerTwo)
    Assert.Equal(2, state.Round)
    Assert.Equal(WaitingForMoves, state.Phase)

/// Symetria: zamiana graczy daje lustrzany wynik (reguły nie faworyzują PlayerOne).
[<Fact>]
let ``rules are symmetric for both players`` () =
    for one in Rules.allCards do
        for two in Rules.allCards do
            let a, _ = Duel.createStandard () |> play one two
            let b, _ = Duel.createStandard () |> play two one
            Assert.Equal(a.PlayerOne, b.PlayerTwo)
            Assert.Equal(a.PlayerTwo, b.PlayerOne)

[<Fact>]
let ``effects describe what happened`` () =
    let effects (one, two) = (Duel.createStandard () |> play one two |> snd).Effects

    Assert.Equal<Effect list>([ ShotHit PlayerOne; ShotHit PlayerTwo ], effects (Shoot, Shoot))
    Assert.Equal<Effect list>([ ShotDodged PlayerOne ], effects (Shoot, Dodge))
    Assert.Equal<Effect list>([ ShotBlocked PlayerOne ], effects (Shoot, Block))
    Assert.Equal<Effect list>([ ShotHit PlayerOne; ReloadInterrupted PlayerTwo ], effects (Shoot, Reload))
    Assert.Equal<Effect list>([ TauntPunished PlayerTwo ], effects (Dodge, Taunt))
    Assert.Equal<Effect list>([ TauntPunished PlayerOne ], effects (Taunt, Block))
    Assert.Equal<Effect list>([ Reloaded PlayerTwo ], effects (Taunt, Reload))
    Assert.Equal<Effect list>([], effects (Taunt, Taunt))
