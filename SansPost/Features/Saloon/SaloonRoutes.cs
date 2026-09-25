namespace SansPost.Features.Saloon
{
    // Dwa adresy doświadczenia SansPost: świadome wejście (Entrance) i główna przestrzeń aplikacji (Saloon).
    // Logo, nawigacja, powroty i domyślne przekierowanie po logowaniu prowadzą do Saloonu, nie do wejścia.
    public static class SaloonRoutes
    {
        public const string Entrance = "/";
        public const string Hub = "/saloon";
    }
}
