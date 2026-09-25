using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SansPost.Infrastructure.Hosting
{
    // Sekcja "ForwardedHeaders": zaufane reverse proxy. X-Forwarded-For / X-Forwarded-Proto są honorowane WYŁĄCZNIE
    // od adresów z KnownProxies / KnownNetworks (plus domyślny loopback). Nagłówki od innych nadawców są ignorowane —
    // klient nie podmieni sobie adresu IP (limity) ani schematu. Bez "trust all".
    public sealed class ReverseProxyOptions
    {
        public const string SectionName = "ForwardedHeaders";

        // Np. "172.30.57.10" (stały adres proxy w sieci Docker).
        public string[] KnownProxies { get; set; } = Array.Empty<string>();

        // CIDR, np. "10.0.0.0/24" — gdy adres proxy nie jest stały.
        public string[] KnownNetworks { get; set; } = Array.Empty<string>();

        // Liczba zaufanych przeskoków (jedno proxy przed aplikacją = 1).
        public int ForwardLimit { get; set; } = 1;

        public static bool IsValidProxy(string value) => IPAddress.TryParse(value, out _);

        public static bool IsValidNetwork(string value)
        {
            var parts = value.Split('/');
            return parts.Length == 2 && IPAddress.TryParse(parts[0], out var address) && int.TryParse(parts[1], out var prefix)
                && prefix >= 0 && prefix <= (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        }

        public void Apply(ForwardedHeadersOptions options)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = ForwardLimit;
            // Domyślne KnownProxies/KnownNetworks (loopback) zostają; dopisujemy tylko jawnie skonfigurowane.
            foreach (var proxy in KnownProxies)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in KnownNetworks)
            {
                var parts = network.Split('/');
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
        }
    }
}
