/// Pomiar czystego silnika: tysiące rozstrzygnięć rundy (czas i alokacje wątku). Informacyjnie, z luźnym limitem.
namespace SansPost.Game.Core.Tests

open System
open System.Diagnostics
open Xunit
open Xunit.Abstractions
open SansPost.Game.Core

[<Trait("Category", "Performance")>]
type PerformanceTests(output: ITestOutputHelper) =

    [<Fact>]
    member _.``100k round resolutions`` () =
        let ready =
            Duel.createStandard ()
            |> Duel.submitMove PlayerOne Shoot
            |> Result.bind (Duel.submitMove PlayerTwo Reload)
            |> Support.ok

        // Rozgrzewka (JIT), potem pomiar.
        for _ in 1..1_000 do Duel.resolveRound ready |> ignore

        let iterations = 100_000
        let allocatedBefore = GC.GetAllocatedBytesForCurrentThread()
        let watch = Stopwatch.StartNew()
        let mutable resolved = 0
        for _ in 1..iterations do
            match Duel.resolveRound ready with
            | Ok _ -> resolved <- resolved + 1
            | Error e -> failwithf "%A" e
        watch.Stop()
        let allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore

        output.WriteLine(sprintf "resolveRound x %d: %d ms, %.3f µs/runda, %d B/runda"
                             iterations watch.ElapsedMilliseconds (watch.Elapsed.TotalMilliseconds * 1000.0 / float iterations) (allocated / int64 iterations))
        Assert.Equal(iterations, resolved)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 5.0, "Rozstrzygnięcie rundy nieoczekiwanie wolne.")

    [<Fact>]
    member _.``10k complete duels`` () =
        let moves = [ Shoot, Reload; Reload, Taunt; Shoot, Dodge; Taunt, Block; Reload, Reload; Shoot, Taunt ]
        let watch = Stopwatch.StartNew()
        for _ in 1..10_000 do
            let final = Duel.createStandard () |> Support.playAll moves
            if final.Phase <> Finished PlayerOneWins then failwith "nieoczekiwany wynik"
        watch.Stop()
        output.WriteLine(sprintf "10k pojedynków po 6 rund: %d ms" watch.ElapsedMilliseconds)
