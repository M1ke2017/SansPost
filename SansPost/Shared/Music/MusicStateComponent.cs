using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SansPost.Shared.Music
{
    // Komponent rysujący stan radia: subskrypcja właściciela audio (js/music.js) po pierwszym renderze, odpięcie przy
    // usunięciu komponentu — Kącik muzyczny otwierany wiele razy nie zostawia nasłuchów ani referencji.
    public abstract class MusicStateComponent : ComponentBase, IAsyncDisposable
    {
        [Inject] protected IJSRuntime JS { get; set; } = default!;

        protected MusicState? State { get; private set; }

        private DotNetObjectReference<MusicStateComponent>? self;
        private int? subscription;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender)
                return;

            self = DotNetObjectReference.Create(this);
            try
            {
                subscription = await JS.InvokeAsync<int>("sansPostMusic.subscribe", self);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
                // Bez skryptu radia (np. stara wersja w pamięci przeglądarki) — komponent zostaje w stanie "zatrzymane".
            }
        }

        [JSInvokable]
        public Task OnMusicState(MusicState state)
        {
            State = state;
            return InvokeAsync(StateHasChanged);
        }

        public virtual async ValueTask DisposeAsync()
        {
            if (subscription is int id)
            {
                try
                {
                    await JS.InvokeVoidAsync("sansPostMusic.unsubscribe", id);
                }
                catch (JSDisconnectedException)
                {
                }
            }
            self?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
