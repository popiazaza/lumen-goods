using System.ComponentModel.DataAnnotations;

namespace Lumen.Domain;

internal static class DomainOptions
{
    /// <summary>
    /// Checks an options object's data annotations. The API validates bound configuration on start; this holds a
    /// hand-built instance (tests, tools) to the same rules, so a domain service never runs with a negative tax rate.
    /// </summary>
    public static T Validated<T>(T options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        return options;
    }
}
