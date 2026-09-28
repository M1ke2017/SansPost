using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SansPost.Features.Music;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Music
{
    // GET /api/music/stations w pełnym pipeline (anonimowo): tylko pola UI; radio niedostępne → 503 z krótkim opisem.
    [Trait("Category", "Music")]
    public class MusicApiTests
    {
        private sealed class RadioFactory : SansPostFactory
        {
            public MusicServiceTests.FakeRadioBrowser Api { get; } = new();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient(RadioBrowserClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api));
            }
        }

        [Fact]
        public async Task Stations_ReturnsOnlyUiFields_Anonymously()
        {
            await using var factory = new RadioFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/music/stations");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var stations = json.RootElement.GetProperty("stations");
            Assert.Equal(3, stations.GetArrayLength());
            var fields = stations[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "bitrate", "codec", "country", "homepage", "name", "stationId", "streamUrl" }, fields);
            Assert.StartsWith("https://", stations[0].GetProperty("streamUrl").GetString());
            Assert.StartsWith("SansPost/", Assert.Single(factory.Api.Requests).Headers.UserAgent.ToString());

            // Drugie wywołanie z pamięci podręcznej — bez zapytania do Radio Browser.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/music/stations")).StatusCode);
            Assert.Single(factory.Api.Requests);
        }

        [Fact]
        public async Task Stations_Unavailable_Returns503WithShortDetail()
        {
            await using var factory = new RadioFactory();
            factory.Api.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/music/stations");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Radio jest chwilowo niedostępne.", json.RootElement.GetProperty("detail").GetString());
        }
    }
}
