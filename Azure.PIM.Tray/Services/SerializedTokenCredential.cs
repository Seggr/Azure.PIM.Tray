using Azure.Core;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Wraps a <see cref="TokenCredential"/> so that all <see cref="GetTokenAsync"/> calls
/// across every instance, tenant, and scope are serialised through a single global gate.
/// This prevents multiple browser windows from popping up when cached tokens expire
/// (e.g. after the user locks their laptop overnight and returns the next day).
/// When the first caller triggers interactive auth, all others wait; once the first
/// succeeds, subsequent callers typically get a cache hit with no popup at all.
/// </summary>
internal sealed class SerializedTokenCredential : TokenCredential
{
    // One gate for the entire process — cached token hits are fast (ms), so the
    // serialisation overhead is negligible, and it guarantees at most one browser
    // window is open at any time regardless of how many tenants/scopes need tokens.
    private static readonly SemaphoreSlim _globalGate = new(1, 1);

    private readonly TokenCredential _inner;

    public SerializedTokenCredential(TokenCredential inner) => _inner = inner;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        _globalGate.Wait(cancellationToken);
        try
        {
            return _inner.GetToken(requestContext, cancellationToken);
        }
        finally
        {
            _globalGate.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        await _globalGate.WaitAsync(cancellationToken);
        try
        {
            return await _inner.GetTokenAsync(requestContext, cancellationToken);
        }
        finally
        {
            _globalGate.Release();
        }
    }
}
