namespace SansPost.Shared.Ui
{
    // Krótkie potwierdzenia istotnych akcji (publikacja, usunięcie, zgłoszenie, moderacja) i błędy operacji w tle.
    // Celowo NIE dla każdej reakcji — polubienie ma własny, natychmiastowy feedback wizualny.
    // Scoped = jeden circuit (jedna karta przeglądarki).
    public sealed class ToastService
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

        private readonly List<ToastMessage> _items = new();
        private int _nextId;

        public event Action? Changed;

        public IReadOnlyList<ToastMessage> Items => _items;

        public void Success(string text) => Show(text, AlertVariant.Success);

        public void Error(string text) => Show(text, AlertVariant.Danger);

        public void Show(string text, AlertVariant variant)
        {
            var toast = new ToastMessage(++_nextId, text, variant);
            _items.Add(toast);
            if (_items.Count > 3)
                _items.RemoveAt(0);

            Changed?.Invoke();
            _ = DismissLaterAsync(toast.Id);
        }

        public void Dismiss(int id)
        {
            if (_items.RemoveAll(t => t.Id == id) > 0)
                Changed?.Invoke();
        }

        private async Task DismissLaterAsync(int id)
        {
            await Task.Delay(Lifetime);
            Dismiss(id);
        }
    }

    public sealed record ToastMessage(int Id, string Text, AlertVariant Variant);
}
