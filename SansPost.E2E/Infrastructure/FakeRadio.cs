using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace SansPost.E2E.Infrastructure
{
    // Sprint 18 — deterministyczne radio w E2E, bez publicznego internetu:
    //  • FakeRadioBrowser: minimalny serwer HTTP (TcpListener, bez uprawnień administratora) udający Radio Browser API —
    //    aplikacja E2E dostaje go w Music__RadioBrowserServers (odkrywanie stacji po stronie serwera);
    //  • FakeRadio.RouteStreamsAsync: strumienie stacji (https://radio.e2e.test/...) obsługuje Playwright w przeglądarce —
    //    krótki sygnał WAV, a "broken" odpowiada 404 (błąd strumienia).
    public sealed class FakeRadioBrowser : IAsyncDisposable
    {
        public sealed record Station(string Id, string Name, string Country, string Stream, string Homepage);

        public static readonly Station[] Stations =
        {
            new("6b8c1f0e-0000-4000-8000-000000000001", "Saloon Country E2E", "The United States Of America", "https://radio.e2e.test/saloon-country.mp3", "https://saloon-country.e2e.test/"),
            new("6b8c1f0e-0000-4000-8000-000000000002", "Prairie Radio E2E", "Canada", "https://radio.e2e.test/prairie.mp3", "https://prairie.e2e.test/"),
            new("6b8c1f0e-0000-4000-8000-000000000003", "Broken Trail FM", "The United States Of America", "https://radio.e2e.test/broken.mp3", "https://broken.e2e.test/")
        };

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public FakeRadioBrowser()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        // Zapytania do "Radio Browser" od wszystkich instancji E2E (pamięć podręczna aplikacji — zwykle 1 na instancję).
        public int Requests => Volatile.Read(ref _requests);

        public string? LastUserAgent { get; private set; }

        private static readonly byte[] Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Stations.Select((s, i) => new Dictionary<string, object?>
        {
            ["changeuuid"] = Guid.NewGuid().ToString(),
            ["stationuuid"] = s.Id,
            ["name"] = s.Name,
            ["url"] = s.Stream,
            ["url_resolved"] = s.Stream,
            ["homepage"] = s.Homepage,
            ["favicon"] = "",
            ["tags"] = "country",
            ["country"] = s.Country,
            ["votes"] = 100 - i,
            ["codec"] = "MP3",
            ["bitrate"] = 128,
            ["hls"] = 0,
            ["lastcheckok"] = 1
        })));

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync() ?? "";
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                            LastUserAgent = line["User-Agent:".Length..].Trim();
                    }

                    var ok = requestLine.StartsWith("GET /json/stations/search?", StringComparison.Ordinal) && requestLine.Contains("tag=country");
                    if (ok)
                        Interlocked.Increment(ref _requests);
                    var body = ok ? Body : Encoding.UTF8.GetBytes("[]");
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch (IOException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
        }
    }

    public static class FakeRadio
    {
        public const string StreamHost = "https://radio.e2e.test/";

        // 6 min cichego sygnału WAV (4 kHz, 8 bit, mono, ~1,4 MB) — dłużej niż najdłuższy test, więc strumień nie kończy się
        // w trakcie (koniec transmisji aplikacja traktuje jak przerwę w nadawaniu stacji).
        private static readonly byte[] Wav = CreateWav(TimeSpan.FromMinutes(6));

        // Liczba pobrań strumienia per stacja (np. brak pętli ponowień po błędzie).
        public sealed class StreamLog
        {
            private readonly Dictionary<string, int> _counts = new();

            public int this[string name] { get { lock (_counts) return _counts.GetValueOrDefault(name); } }

            internal void Hit(string name)
            {
                lock (_counts)
                    _counts[name] = _counts.GetValueOrDefault(name) + 1;
            }
        }

        public static async Task<StreamLog> RouteStreamsAsync(IBrowserContext context)
        {
            var log = new StreamLog();
            await context.RouteAsync(StreamHost + "**", async route =>
            {
                var name = new Uri(route.Request.Url).AbsolutePath.Trim('/');
                log.Hit(name);
                if (name.StartsWith("broken", StringComparison.Ordinal))
                    await route.FulfillAsync(new() { Status = 404, Body = "gone" });
                else
                    await route.FulfillAsync(new() { Status = 200, ContentType = "audio/wav", BodyBytes = Wav, Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" } });
            });
            return log;
        }

        private static byte[] CreateWav(TimeSpan length)
        {
            const int rate = 4000;
            var samples = (int)(rate * length.TotalSeconds);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + samples);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);      // PCM
            w.Write((short)1);      // mono
            w.Write(rate);
            w.Write(rate);          // bajty/s
            w.Write((short)1);
            w.Write((short)8);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(samples);
            for (var i = 0; i < samples; i++)
                w.Write((byte)(128 + (i % 40 < 20 ? 1 : -1)));   // bardzo cichy sygnał
            w.Flush();
            return ms.ToArray();
        }
    }
}
