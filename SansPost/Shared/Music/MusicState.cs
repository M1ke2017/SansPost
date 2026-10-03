using SansPost.Features.Music;

namespace SansPost.Shared.Music
{
    // Stan radia z właściciela audio w przeglądarce (js/music.js) — komponenty Blazor go tylko rysują.
    // Status: idle (jeszcze nie grało) | loading (łączenie, buforowanie) | playing | paused | error (stacja nie odpowiada).
    public sealed record MusicState(string? StationId, string? Name, string? Country, string? Codec, int? Bitrate, string? Homepage,
        string? StreamUrl, string Status, int Volume, bool Started)
    {
        public bool IsActive => Status is "playing" or "loading";

        public string StatusText => TextFor(Status);

        public static string TextFor(string status) => status switch
        {
            "playing" => "Radio gra",
            "loading" => "Łączenie ze stacją…",
            "error" => "Ta stacja jest chwilowo niedostępna.",
            // Limit aktywnego grania (js/music.js) — informacja, nie błąd.
            "limit" => "Radio zostało zatrzymane po 2 godzinach odtwarzania.",
            _ => "Radio zatrzymane"
        };

        // Atrybuty stacji dla przycisków sterowanych przez js/music.js (Graj / Pauza, wybór stacji).
        public static IReadOnlyDictionary<string, object> Attributes(string id, string name, string? country, string? codec, int? bitrate,
            string? homepage, string streamUrl) => new Dictionary<string, object>
        {
            ["data-station-id"] = id,
            ["data-station-name"] = name,
            ["data-station-url"] = streamUrl,
            ["data-station-country"] = country ?? "",
            ["data-station-codec"] = codec ?? "",
            ["data-station-bitrate"] = bitrate?.ToString() ?? "",
            ["data-station-homepage"] = homepage ?? ""
        };

        public static IReadOnlyDictionary<string, object> Attributes(RadioStationResponse station) =>
            Attributes(station.StationId, station.Name, station.Country, station.Codec, station.Bitrate, station.Homepage, station.StreamUrl);

        public IReadOnlyDictionary<string, object> StationAttributes() =>
            Attributes(StationId ?? "", Name ?? "Radio country", Country, Codec, Bitrate, Homepage, StreamUrl ?? "");
    }
}
