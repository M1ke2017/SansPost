using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SansPost.Features.Saloon;

namespace SansPost.Shared.Bar
{
    // Poruszanie się po poziomach BAR przez historię przeglądarki (bez własnej maszyny routingu).
    // Każdy wpis baru dodany w tej sesji niesie w HistoryEntryState swoją głębokość i to, skąd wyszedł bar:
    // "sp-bar:{głębokość}:{h|d}" — h: z sali (/saloon bez baru), d: z linku prosto do baru. Dzięki temu:
    //   w górę (przycisk "wróć", Escape) = history.back() — "wstecz" w przeglądarce działa identycznie,
    //   zamknięcie baru = cofnięcie dokładnie do wpisu sali (bez mnożenia wpisów),
    //   link wklejony z zewnątrz (brak stanu) — w górę zastępuje wpis rodzicem, więc nie ma "losowego" stanu panelu.
    public static class BarNavigation
    {
        private const string Prefix = "sp-bar:";

        private static (int Depth, bool FromHall) Read(NavigationManager navigation)
        {
            var parts = navigation.HistoryEntryState?.Split(':');
            return parts is [ "sp-bar", var depth, var root ] && int.TryParse(depth, out var n) && n > 0 ? (n, root == "h") : (0, false);
        }

        // Nowy poziom (Karta rozmów z sali, lista lub wyszukiwanie z Karty) — nowy wpis historii.
        public static void Push(NavigationManager navigation, BarState state)
        {
            var (depth, fromHall) = Read(navigation);
            var root = depth > 0 ? fromHall : !IsBarUrl(navigation.Uri);
            navigation.NavigateTo(state.Url, new NavigationOptions { HistoryEntryState = $"{Prefix}{depth + 1}:{(root ? "h" : "d")}" });
        }

        // Ten sam poziom, inne parametry (sortowanie, fraza wyszukiwania, filtr tematu) — bez nowego wpisu historii.
        public static void Replace(NavigationManager navigation, BarState state) =>
            navigation.NavigateTo(state.Url, new NavigationOptions { ReplaceHistoryEntry = true, HistoryEntryState = navigation.HistoryEntryState });

        public static async Task UpAsync(NavigationManager navigation, IJSRuntime js, BarState current)
        {
            if (Read(navigation).Depth > 0)
                await js.InvokeVoidAsync("sansPost.historyGo", -1);
            else
                navigation.NavigateTo(current.Parent?.Url ?? SaloonRoutes.Hub, new NavigationOptions { ReplaceHistoryEntry = true });
        }

        public static async Task CloseAsync(NavigationManager navigation, IJSRuntime js)
        {
            var (depth, fromHall) = Read(navigation);
            if (depth > 0 && fromHall)
                await js.InvokeVoidAsync("sansPost.historyGo", -depth);
            else
                navigation.NavigateTo(SaloonRoutes.Hub, new NavigationOptions { ReplaceHistoryEntry = true });
        }

        private static bool IsBarUrl(string uri) =>
            uri.Contains("/saloon?", StringComparison.OrdinalIgnoreCase) && uri.Contains("bar=", StringComparison.OrdinalIgnoreCase);
    }
}
