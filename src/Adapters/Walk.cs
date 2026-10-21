using Minio.DataModel;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// A listing being walked, one entry at a time, with its failures answered as data.
/// </summary>
/// <param name="items">The entries the SDK yields.</param>
internal sealed class Walk(IAsyncEnumerator<Item> items) : IAsyncDisposable
{
    /// <summary>Gets the entry the walk stands on, or <see langword="null"/> once it has ended.</summary>
    public Item? Current { get; private set; }

    /// <summary>
    /// Moves to the next entry.
    /// </summary>
    /// <param name="cancellationToken">The caller's token, whose cancellation is rethrown rather than answered.</param>
    /// <returns><see langword="null"/> when the walk moved on or ended, or why it could not.</returns>
    public async ValueTask<StorageError?> AdvanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            Current = await items.MoveNextAsync().ConfigureAwait(false) ? items.Current : null;

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Current = null;

            return MinioErrors.From(exception);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => items.DisposeAsync();
}
