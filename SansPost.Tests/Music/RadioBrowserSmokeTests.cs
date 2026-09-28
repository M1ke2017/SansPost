using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features.Music;

namespace SansPost.Tests.Music
{
    // Prawdziwe Radio Browser (sieć) — osobny smoke test, NIE część zwykłego przebiegu: uruchamiany świadomie
    // (SANSPOST_RADIO_SMOKE=1). Sprawdza, że publiczne API nadal daje działające stacje country w oczekiwanym kształcie.
    [Trait("Category", "ExternalSmoke")]
    public class RadioBrowserSmokeTests
    {
        public sealed class RadioSmokeFactAttribute : FactAttribute
        {
            public RadioSmokeFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("SANSPOST_RADIO_SMOKE") != "1")
                    Skip = "Smoke test prawdziwego Radio Browser — uruchom z SANSPOST_RADIO_SMOKE=1.";
            }
        }

        [RadioSmokeFact]
        public async Task RealRadioBrowser_ReturnsCountryStations()
        {
            var services = new ServiceCollection();
            var options = new MusicOptions();
            services.AddHttpClient(RadioBrowserClient.HttpClientName, http => http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent));
            await using var provider = services.BuildServiceProvider();
            var client = new RadioBrowserClient(provider.GetRequiredService<IHttpClientFactory>(), Options.Create(options), NullLogger<RadioBrowserClient>.Instance);
            var service = new MusicService(client, new MemoryCache(new MemoryCacheOptions()), Options.Create(options), NullLogger<MusicService>.Instance);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.GetStationsAsync();
            var miss = watch.ElapsedMilliseconds;
            watch.Restart();
            var cached = await service.GetStationsAsync();

            Assert.True(result.Available, "Radio Browser niedostępny.");
            Assert.InRange(result.Stations.Count, 3, MusicLimits.StationCount);
            Assert.All(result.Stations, s => Assert.StartsWith("https://", s.StreamUrl));
            Assert.Equal(MusicStationsSource.Cache, cached.Source);
            Console.WriteLine($"cache miss {miss} ms, cache hit {watch.Elapsed.TotalMilliseconds:F2} ms");
            foreach (var s in result.Stations)
                Console.WriteLine($"{s.Name} | {s.Country} | {s.Codec} {s.Bitrate} | {s.StreamUrl}");
        }
    }
}
