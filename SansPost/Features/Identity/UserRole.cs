namespace SansPost.Features.Identity
{
    // Rola określa uprawnienia. Poziom subskrypcji to osobny koncept (SubscriptionType).
    // Wartości są zapisywane w bazie jako integer — nie zmieniaj ich bez migracji.
    public enum UserRole
    {
        User = 0,
        Admin = 1
    }
}
