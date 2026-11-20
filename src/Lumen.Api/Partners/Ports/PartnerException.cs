namespace Lumen.Api.Partners.Ports;

/// <summary>A partner could not be read. The message is safe to show on the admin page (no credentials).</summary>
public class PartnerException(string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The partner's HTTP status, when it answered at all.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>The partner answered, but with a document Lumen refuses to apply (malformed, DTD, truncated, mass deletion).</summary>
public sealed class PartnerDataException(string message, Exception? innerException = null)
    : PartnerException(message, null, innerException);
