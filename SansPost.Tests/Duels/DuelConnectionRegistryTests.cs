using SansPost.Features.Duels;

namespace SansPost.Tests.Duels
{
    // Sprint 20 — kto jest przy stole: kilka kart jednego gracza, ostatnie połączenie, uchwyty bez UserId, wątki.
    [Trait("Category", "Duels")]
    public class DuelConnectionRegistryTests
    {
        [Fact]
        public void SecondTab_KeepsPlayerOnline_UntilLastConnectionCloses()
        {
            var registry = new DuelConnectionRegistry();

            Assert.True(registry.Add(7, "Anna", "tab-1"));
            Assert.False(registry.Add(7, "Anna", "tab-2"));
            var handle = registry.Online().Single().Handle;

            Assert.Null(registry.Remove("tab-1"));
            Assert.True(registry.IsOnline(7));
            Assert.Equal(7, registry.UserOf(handle));

            Assert.Equal(7, registry.Remove("tab-2"));
            Assert.False(registry.IsOnline(7));
            Assert.Null(registry.UserOf(handle));
            Assert.Equal((0, 0), (registry.UserCount, registry.ConnectionCount));
            Assert.Null(registry.Remove("tab-2"));   // podwójne rozłączenie — bez efektu
        }

        [Fact]
        public void Handles_AreOpaque_AndUnique()
        {
            var registry = new DuelConnectionRegistry();
            for (var user = 1; user <= 200; user++)
                registry.Add(user, $"Gracz{user}", $"c{user}");

            var online = registry.Online();
            Assert.Equal(200, online.Select(p => p.Handle).Distinct().Count());
            Assert.All(online, p => Assert.Matches("^[0-9a-f]{16}$", p.Handle));
            Assert.Null(registry.UserOf("1"));
        }

        [Fact]
        public async Task ConcurrentConnectsAndDisconnects_LeaveConsistentState()
        {
            var registry = new DuelConnectionRegistry();
            var firsts = 0;
            var lasts = 0;

            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 2_000; i++)
                {
                    var user = i % 25;
                    var connection = $"w{worker}-{i}";
                    if (registry.Add(user, "Gracz", connection))
                        Interlocked.Increment(ref firsts);
                    if (registry.Remove(connection) is not null)
                        Interlocked.Increment(ref lasts);
                }
            })));

            Assert.Equal(firsts, lasts);   // każde "pojawił się" ma swoje "odszedł"
            Assert.Equal((0, 0), (registry.UserCount, registry.ConnectionCount));
        }
    }
}
