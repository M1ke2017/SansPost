using System.ComponentModel.DataAnnotations;

namespace SansPost.Features
{
    // Waliduje request DTO tymi samymi DataAnnotations, których używa [ApiController].
    // Dzięki temu Blazor (który woła serwisy bezpośrednio) podlega tym samym regułom co REST API.
    public static class RequestValidator
    {
        public static string? Validate(object request)
        {
            var results = new List<ValidationResult>();
            var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

            return isValid ? null : string.Join(" ", results.Select(r => r.ErrorMessage));
        }
    }
}
