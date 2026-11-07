using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The newest hashed row of a chain.</summary>
/// <param name="ChainIndex">Its position in the chain.</param>
/// <param name="Sequence">Its sequence number (the id of the row in its table).</param>
/// <param name="PayloadHash">Its hash.</param>
public sealed record ChainHead(long ChainIndex, long Sequence, string PayloadHash);

/// <summary>
/// Cross-module port: verify the hash chain of ONE module's database. The host registers one source per module, so an auditor's tool
/// can check every module's evidence without touching another module's DbContext. Later phases add sources for the activity log and
/// the mutation intents with the same shape.
/// </summary>
public interface IAuditChainSource
{
    /// <summary>The module name: for example <c>orders</c> or <c>billing</c>.</summary>
    string Module { get; }

    /// <summary>A full walk of the chain (streaming, bounded memory). Heavy: run it rarely and audited.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>A bounded step of the verification (see <see cref="AuditChain.VerifyAsync"/>). A source that cannot resume verifies the whole chain.</summary>
    /// <param name="afterChainIndex">Resume after this chain index.</param>
    /// <param name="maxRows">Stop after this many rows.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<AuditChainVerification> VerifyRangeAsync(long afterChainIndex, int maxRows, CancellationToken cancellationToken) => VerifyAsync(cancellationToken);

    /// <summary>Compares the anchors of this module with its table (see <see cref="AuditChain.VerifyAnchorsAsync"/>). A source without anchors checks nothing.</summary>
    /// <param name="anchors">The anchors of this module.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many were checked and which no longer match.</returns>
    Task<AnchorCheck> VerifyAnchorsAsync(IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken) => Task.FromResult(AnchorCheck.None);

    /// <summary>The newest hashed row, or null when nothing is hashed yet.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The head, or null.</returns>
    Task<ChainHead?> GetHeadAsync(CancellationToken cancellationToken) => Task.FromResult<ChainHead?>(null);
}

/// <summary>All chain sources of the host, by module name.</summary>
public interface IAuditChainSources
{
    /// <summary>The module names, in registration order.</summary>
    IReadOnlyList<string> Modules { get; }

    /// <summary>Finds a source by module name (case-insensitive); null when there is none. Callers turn null into <c>Chain.UnknownModule</c>.</summary>
    /// <param name="module">The module name.</param>
    /// <returns>The source, or null.</returns>
    IAuditChainSource? Find(string module);
}

internal sealed class AuditChainSources(IEnumerable<IAuditChainSource> sources) : IAuditChainSources
{
    private readonly List<IAuditChainSource> _sources = sources.ToList();

    public IReadOnlyList<string> Modules => _sources.Select(s => s.Module).ToList();

    public IAuditChainSource? Find(string module)
        => _sources.FirstOrDefault(s => string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The source of one module, on that module's <see cref="AuditableDbContext"/> and the host's shared key ring.</summary>
/// <typeparam name="TDbContext">The audited context.</typeparam>
/// <param name="module">The module name.</param>
/// <param name="scopeFactory">Opens a scope per call.</param>
/// <param name="chain">The chain.</param>
public sealed class AuditChainSource<TDbContext>(string module, IServiceScopeFactory scopeFactory, AuditChain chain) : IAuditChainSource
    where TDbContext : AuditableDbContext
{
    /// <inheritdoc />
    public string Module { get; } = module;

    /// <inheritdoc />
    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await chain.VerifyAsync(db, cancellationToken, module: Module);
    }

    /// <inheritdoc />
    public async Task<AuditChainVerification> VerifyRangeAsync(long afterChainIndex, int maxRows, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await chain.VerifyAsync(db, cancellationToken, afterChainIndex: afterChainIndex, maxRows: maxRows, module: Module);
    }

    /// <inheritdoc />
    public async Task<AnchorCheck> VerifyAnchorsAsync(IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await chain.VerifyAnchorsAsync(db, anchors, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ChainHead?> GetHeadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var head = await chain.GetHeadAsync(db, cancellationToken);
        return head is null ? null : new ChainHead(head.Value.ChainIndex, head.Value.Sequence, head.Value.PayloadHash);
    }
}
