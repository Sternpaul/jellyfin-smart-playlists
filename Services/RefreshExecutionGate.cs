using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AIRecommender.Services;

public sealed class RefreshExecutionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
