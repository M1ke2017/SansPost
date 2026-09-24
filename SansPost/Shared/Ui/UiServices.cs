namespace SansPost.Shared.Ui
{
    // Blazor Server: scope DI = cały circuit, więc zwykłe @inject dawałoby JEDEN DbContext współdzielony przez wszystkie
    // komponenty strony (layout, feed, panel boczny) ładujące dane równolegle → "A second operation was started on this
    // context". Każda operacja UI dostaje więc własny, krótki scope (świeży DbContext, brak nieaktualnych encji
    // w change trackerze). Serwisy domenowe i REST API pozostają bez zmian (tam scope = żądanie HTTP).
    public sealed class UiServices
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public UiServices(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<TResult> Run<TService, TResult>(Func<TService, Task<TResult>> operation) where TService : notnull
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await operation(scope.ServiceProvider.GetRequiredService<TService>());
        }
    }
}
