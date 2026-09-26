namespace SansPost.Shared
{
    // Który renderer przestrzeni SansPost (Entrance, Saloon Main Hall) wybrała przeglądarka: "3d" | "css" | null (nieznany).
    // Źródła: ciasteczko "sp-scene" (ustawia js/scene3d-common.js po decyzji — prerender /saloon od razu w dobrym trybie)
    // i decyzja Entrance w tym samym circuicie (gość wchodzi przez drzwi bez przeładowania). Wyłącznie preferencja wyglądu.
    public sealed class ScenePreference
    {
        public const string CookieName = "sp-scene";

        public string? Renderer { get; set; }

        public static string? Parse(string? value) => value is "3d" or "css" ? value : null;
    }
}
