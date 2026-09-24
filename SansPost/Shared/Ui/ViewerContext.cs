using SansPost.Features.Identity;

namespace SansPost.Shared.Ui
{
    // Kto ogląda stronę — kaskadowane z MainLayout. null = gość.
    // Wyłącznie do decyzji prezentacyjnych (co pokazać); autoryzacja zawsze po stronie serwisów.
    public sealed record ViewerContext(int UserId, string Username, bool IsAdmin, AccountStatus? Status)
    {
        // Status nieznany (jeszcze wczytywany) traktujemy jak aktywny — serwer i tak rozstrzyga przy zapisie.
        public bool CanWrite => Status is null or AccountStatus.Active;

        public bool IsSuspended => Status == AccountStatus.Suspended;
    }
}
