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
        PreconditionFailed
    }

    // Wynik operacji use-case, wspólny dla Controllers i Blazor UI.
    public class ServiceResult
    {
        protected ServiceResult(ServiceError error, string? message)
        {
            Error = error;
            Message = message;
        }

        public ServiceError Error { get; }
        public string? Message { get; }
        public bool Succeeded => Error == ServiceError.None;

        public static ServiceResult Success() => new(ServiceError.None, null);

        public static ServiceResult Fail(ServiceError error, string message) => new(error, message);
    }

    public sealed class ServiceResult<T> : ServiceResult
    {
        private ServiceResult(T? value, ServiceError error, string? message) : base(error, message)
        {
            Value = value;
        }

        public T? Value { get; }

        public static ServiceResult<T> Success(T value) => new(value, ServiceError.None, null);

        public static new ServiceResult<T> Fail(ServiceError error, string message) => new(default, error, message);
    }
}
