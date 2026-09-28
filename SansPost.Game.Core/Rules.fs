/// Reguły kart: jedna runda to jednoczesne rozstrzygnięcie dwóch odsłoniętych kart. Czyste funkcje, bez stanu.
module SansPost.Game.Core.Rules

let standard =
    { StartPrestige = 3
      StartAmmo = 1
      MaxAmmo = 2
      MaxRounds = 12 }

let opponentOf =
    function
    | PlayerOne -> PlayerTwo
    | PlayerTwo -> PlayerOne

/// Strzał jest legalny tylko z nabojem.
let canPlay (state: PlayerState) card =
    match card with
    | Shoot -> state.Ammo >= 1
    | Dodge
    | Reload
    | Block
    | Taunt -> true

let allCards = [ Shoot; Dodge; Reload; Block; Taunt ]

let available (state: PlayerState) = allCards |> List.filter (canPlay state)

/// Czy strzał przeciwnika trafia gracza, który zagrał `card`: unik i blok chronią, reszta zostaje trafiona.
let private hitBy opponentCard card =
    opponentCard = Shoot
    && (match card with
        | Dodge
        | Block -> false
        | Shoot
        | Reload
        | Taunt -> true)

/// Prowokacja karze defensywę: unik i blok tracą prestiż.
let private tauntedOut opponentCard card =
    opponentCard = Taunt
    && (match card with
        | Dodge
        | Block -> true
        | Shoot
        | Reload
        | Taunt -> false)

/// Nowy stan jednego gracza po rundzie (jego karta vs karta przeciwnika).
let private settle (rules: DuelRules) (state: PlayerState) card opponentCard =
    let hit = hitBy opponentCard card
    let losses = (if hit then 1 else 0) + (if tauntedOut opponentCard card then 1 else 0)
    let spent = if card = Shoot then 1 else 0
    let reloaded = if card = Reload && not hit then 1 else 0

    { Prestige = state.Prestige - losses
      Ammo = min rules.MaxAmmo (state.Ammo - spent + reloaded) }

/// Efekty widziane z perspektywy gracza `me` (jego karta i to, co zrobiła z nim karta przeciwnika).
let private effectsOf (rules: DuelRules) me (state: PlayerState) card opponentCard =
    let opponent = opponentOf me

    [ if card = Shoot then
          match opponentCard with
          | Dodge -> ShotDodged me
          | Block -> ShotBlocked me
          | Shoot
          | Reload
          | Taunt -> ShotHit me
      if card = Reload then
          if hitBy opponentCard card then ReloadInterrupted me
          elif state.Ammo >= rules.MaxAmmo then ReloadAtMax me
          else Reloaded me
      if tauntedOut opponentCard card then
          TauntPunished opponent ]

/// Jednoczesne rozstrzygnięcie dwóch odsłoniętych kart: nowy stan obu graczy i lista efektów.
let resolveCards (rules: DuelRules) (one: PlayerState, oneCard) (two: PlayerState, twoCard) =
    let oneAfter = settle rules one oneCard twoCard
    let twoAfter = settle rules two twoCard oneCard
    let effects = effectsOf rules PlayerOne one oneCard twoCard @ effectsOf rules PlayerTwo two twoCard oneCard
    oneAfter, twoAfter, effects

/// Runda bez karty (koniec czasu): spóźniony gracz traci 1 prestiżu; nikt nie strzela i nie przeładowuje.
/// Karta gracza, który zdążył, przepada bez efektu (nie zużywa naboju) — kara dotyczy tylko spóźnionego.
let timeoutPenalty (state: PlayerState) = { state with Prestige = state.Prestige - 1 }

/// Zwycięzca pojedynku (None = remis). Oddanie pojedynku wygrywa przeciwnik oddającego.
let winner =
    function
    | PlayerOneWins -> Some PlayerOne
    | PlayerTwoWins -> Some PlayerTwo
    | Draw -> None
    | Forfeited loser -> Some(opponentOf loser)

/// Wynik po rundzie: nokaut (obaj — remis), limit rund (więcej prestiżu), inaczej pojedynek trwa.
let outcome (rules: DuelRules) round (one: PlayerState) (two: PlayerState) =
    match one.Prestige <= 0, two.Prestige <= 0 with
    | true, true -> Some Draw
    | true, false -> Some PlayerTwoWins
    | false, true -> Some PlayerOneWins
    | false, false when round >= rules.MaxRounds ->
        Some(
            if one.Prestige > two.Prestige then PlayerOneWins
            elif two.Prestige > one.Prestige then PlayerTwoWins
            else Draw
        )
    | false, false -> None
