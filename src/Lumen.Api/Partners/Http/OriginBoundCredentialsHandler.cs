namespace Lumen.Api.Partners;

/// <summary>
/// Adds a partner's credentials to a request only when it goes to that partner's configured origin, and strips them
/// otherwise. A partner URL that points elsewhere (an image on another host, a redirect target) never receives the
/// key. Redirects are not followed at all (the primary handler has AllowAutoRedirect off).
/// </summary>
internal abstract class OriginBoundCredentialsHandler : DelegatingHandler
{
    protected abstract Uri? PartnerOrigin { get; }

    protected abstract void AddCredentials(HttpRequestMessage request);

    protected abstract void RemoveCredentials(HttpRequestMessage request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RemoveCredentials(request);
        if (PartnerOrigin is { } origin && request.RequestUri is { } target && PartnerUrls.IsSameOrigin(target, origin))
        {
            AddCredentials(request);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
