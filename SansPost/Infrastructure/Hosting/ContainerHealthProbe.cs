namespace SansPost.Infrastructure.Hosting
{
    // HEALTHCHECK obrazu: obraz runtime celowo nie zawiera curl/wget, więc sondę wykonuje sama aplikacja.
    // Pyta lokalny /health/ready (gotowość = proces + PostgreSQL); kod 0 = zdrowy, 1 = niezdrowy.
    public static class ContainerHealthProbe
    {
        public static async Task<int> RunAsync()
        {
            var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
            var port = ports.Split(';', ',')[0].Trim();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/health/ready");
                return response.IsSuccessStatusCode ? 0 : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }
}
