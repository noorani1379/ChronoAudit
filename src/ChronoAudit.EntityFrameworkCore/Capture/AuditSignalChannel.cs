using System.Threading.Channels;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// A tiny in-memory signal that wakes the hash worker as soon as audit rows were saved, so it does not have to poll the database.
/// Register it as a singleton and share the instance between the contexts and the worker.
/// </summary>
public sealed class AuditSignalChannel
{
    // DropOldest with capacity one: the worker only needs to know "there is something new". A thousand concurrent saves never fill the
    // queue; they all collapse into one pending wake-up.
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Called after audit rows were committed, to wake the worker.</summary>
    public void NotifyNewAudit()
    {
        _channel.Writer.TryWrite(true);
    }

    /// <summary>Read by the worker: yields once per wake-up and sleeps while there is nothing new.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>One item per wake-up.</returns>
    public IAsyncEnumerable<bool> WaitAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
