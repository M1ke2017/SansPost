/// Manekin do pojedynku treningowego: deterministyczny, jawny scenariusz (bez losowości i bez "AI").
/// Widzi wyłącznie własny DuelView — nie ma dostępu do ukrytej karty gracza, więc gra uczciwie.
module SansPost.Game.Core.TrainingDummy

let private script = [| Reload; Shoot; Block; Taunt; Dodge; Shoot |]

/// Karta manekina na bieżącą rundę: kolejna ze scenariusza; bez naboju zamiast strzału — przeładowanie.
let chooseCard (view: DuelView) =
    let planned = script.[(view.Round - 1) % script.Length]
    if Rules.canPlay view.Mine planned then planned else Reload
