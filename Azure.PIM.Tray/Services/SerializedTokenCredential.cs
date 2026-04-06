using System.Collections.Concurrent;
using Azure.Core;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Wraps a <see cref="TokenCredential"/> so that concurrent <see cref="GetTokenAsync"/>
/// calls for the same scope are serialised through a per-scope <see cref="SemaphoreSlim"/>.
/// Prevents multiple browser windows from popping up during initial sign-in.
/// </summary>
internal sealed class SerializedTokenCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public SerializedTokenCredential(TokenCredential inner) => _inner = inner;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => _inner.GetToken(requestContext, cancellationToken);

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var key  = string.Join(" ", requestContext.Scopes);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _inner.GetTokenAsync(requestContext, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
