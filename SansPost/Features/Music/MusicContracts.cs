namespace SansPost.Features.Music
{
    // Stacja dla UI — tylko to, co pokazuje Kącik muzyczny. Bez pól technicznych Radio Browser (głosy, kliknięcia,
    // czasy sprawdzeń, identyfikatory zmian). StreamUrl zawsze https; Homepage tylko http/https albo null.
    public sealed record RadioStationResponse(
        string StationId,
        string Name,
        string? Country,
        string? Homepage,
        string StreamUrl,
        string? Codec,
        int? Bitrate);

    public sealed record RadioStationsResponse(IReadOnlyList<RadioStationResponse> Stations);

    // Wynik dla UI i API: Available = false tylko wtedy, gdy nie ma ani świeżej, ani ostatniej poprawnej listy.
    public sealed record MusicStationsResult(IReadOnlyList<RadioStationResponse> Stations, bool Available, MusicStationsSource Source);

    public enum MusicStationsSource
    {
        Fresh,
        Cache,
        LastKnownGood,
        Unavailable
    }

    public static class MusicLimits
    {
        public const int CandidateLimit = 40;   // tyle rekordów bierzemy z API (po głosach), z nich wybieramy StationCount
        public const int StationCount = 10;
        public const int NameMaxLength = 80;
        public const int CountryMaxLength = 60;
        public const int MinBitrate = 48;
        public const int MaxBitrate = 320;
    }
}
