namespace ChronoAudit;

/// <summary>The outcome of an operation that can fail in a predictable way (no exception for expected failures).</summary>
public class ChronoAuditResult
{
    private readonly ChronoAuditError? _error;

    /// <summary>Creates a result; a null <paramref name="error"/> means success.</summary>
    /// <param name="error">The failure, or null.</param>
    /// <exception cref="ArgumentException">The error has an empty code or message.</exception>
    protected ChronoAuditResult(ChronoAuditError? error)
    {
        if (error is not null)
        {
            // A failure without a code or message cannot be mapped or logged, so refuse it at the single gate.
            ArgumentException.ThrowIfNullOrWhiteSpace(error.Code, nameof(error));
            ArgumentException.ThrowIfNullOrWhiteSpace(error.Message, nameof(error));
        }

        _error = error;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public ChronoAuditError Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Creates a successful result.</summary>
    /// <returns>The result.</returns>
    public static ChronoAuditResult Success() => new(null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static ChronoAuditResult Failure(ChronoAuditError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ChronoAuditResult(error);
    }

    /// <summary>Creates a successful result that carries a value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static ChronoAuditResult<T> Success<T>(T value) => new(value, null);

    /// <summary>Creates a failed result of a value type.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="error">The failure.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static ChronoAuditResult<T> Failure<T>(ChronoAuditError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ChronoAuditResult<T>(default, error);
    }
}

/// <summary>A <see cref="ChronoAuditResult"/> that carries a value when it succeeds.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class ChronoAuditResult<T> : ChronoAuditResult
{
    private readonly T? _value;

    internal ChronoAuditResult(T? value, ChronoAuditError? error)
        : base(error)
    {
        _value = value;
    }

    /// <summary>The value.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>A value converts to a successful result.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator ChronoAuditResult<T>(T value) => Success(value);

    /// <summary>An error converts to a failed result.</summary>
    /// <param name="error">The error.</param>
    public static implicit operator ChronoAuditResult<T>(ChronoAuditError error) => Failure<T>(error);
}
