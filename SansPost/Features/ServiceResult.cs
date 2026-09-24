namespace SansPost.Features
{
    public enum ServiceError
    {
        None,
        Validation,
        NotFound,
        Forbidden,
        Conflict,

        // Optimistic concurrency: klient edytował nieaktualną wersję zasobu.
        PreconditionFailed,

        // Limit operacji zapisu użytkownika (RetryAfter wskazuje, kiedy ponowić).
        RateLimited
    }

    // Wynik operacji use-case, wspólny dla Controllers i Blazor UI.
    // Code — stabilny, maszynowy identyfikator problemu (np. "registration-capacity-reached") dla klientów API.
    public class ServiceResult
    {
        protected ServiceResult(ServiceError error, string? message, string? code, TimeSpan? retryAfter)
        {
            Error = error;
            Message = message;
            Code = code;
            RetryAfter = retryAfter;
        }

        public ServiceError Error { get; }
        public string? Message { get; }
        public string? Code { get; }
        public TimeSpan? RetryAfter { get; }
        public bool Succeeded => Error == ServiceError.None;

        public static ServiceResult Success() => new(ServiceError.None, null, null, null);

        public static ServiceResult Fail(ServiceError error, string message, string? code = null, TimeSpan? retryAfter = null) =>
            new(error, message, code, retryAfter);
    }

    public sealed class ServiceResult<T> : ServiceResult
    {
        private ServiceResult(T? value, ServiceError error, string? message, string? code, TimeSpan? retryAfter)
            : base(error, message, code, retryAfter)
        {
            Value = value;
        }

        public T? Value { get; }

        public static ServiceResult<T> Success(T value) => new(value, ServiceError.None, null, null, null);

        public static new ServiceResult<T> Fail(ServiceError error, string message, string? code = null, TimeSpan? retryAfter = null) =>
            new(default, error, message, code, retryAfter);

        // Przeniesienie niepowodzenia (np. z WriteGuard) bez utraty kodu i Retry-After.
        public static ServiceResult<T> From(ServiceResult failure) =>
            new(default, failure.Error, failure.Message, failure.Code, failure.RetryAfter);
    }
}
