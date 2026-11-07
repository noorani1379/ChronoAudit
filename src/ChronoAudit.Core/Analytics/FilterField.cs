using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace ChronoAudit;

/// <summary>
/// One filterable field of an analytic subject: a catalog key, the column it reads (an expression written in code, never built from user
/// text), the operators it allows and how to turn the typed text of a request into a value of the column's type.
/// </summary>
public interface IFilterField<TRow>
{
    /// <summary>The key.</summary>
    string Key { get; }

    /// <summary>The Persian title used in messages.</summary>
    string Title { get; }

    /// <summary>The operators.</summary>
    IReadOnlySet<FilterOp> Operators { get; }

    /// <summary>The predicate for one condition, or a typed refusal.</summary>
    ChronoAuditResult<Expression<Func<TRow, bool>>> Build(FilterOp op, IReadOnlyList<string> values, FilterLimits limits, ITextNormalizer normalizer);
}

/// <summary>The filter field.</summary>
public static class FilterField
{
    /// <summary>A field over a column of a plain type (numbers, ids, booleans, dates, enums), possibly nullable.</summary>
    public static IFilterField<TRow> Of<TRow, TValue>(string key, string title, Expression<Func<TRow, TValue>> column, params FilterOp[] operators)
        => new FilterField<TRow, TValue>(key, title, column, operators, normalizeText: false);

    /// <summary>
    /// A text field. <paramref name="column"/> must be the NORMALIZED column (the <c>FullNameNormalized</c> pattern): the request value is
    /// normalized with <see cref="ITextNormalizer"/> here, once, so Arabic and Persian yeh/kaf and digits agree. Never
    /// point it at a raw column.
    /// </summary>
    public static IFilterField<TRow> NormalizedText<TRow>(string key, string title, Expression<Func<TRow, string?>> column, params FilterOp[] operators)
        => new FilterField<TRow, string?>(key, title, column, operators, normalizeText: true);
}

internal sealed class FilterField<TRow, TValue> : IFilterField<TRow>
{
    private static readonly Type ValueType = typeof(TValue);
    private static readonly Type Underlying = Nullable.GetUnderlyingType(ValueType) ?? ValueType;
    private static readonly bool IsNullable = !ValueType.IsValueType || Nullable.GetUnderlyingType(ValueType) is not null;

    private static readonly MethodInfo ContainsMethod = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(ValueType);

    private static readonly MethodInfo StartsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private readonly Expression<Func<TRow, TValue>> _column;
    private readonly bool _normalizeText;

    public FilterField(string key, string title, Expression<Func<TRow, TValue>> column, IReadOnlyCollection<FilterOp> operators, bool normalizeText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(column);
        if (operators.Count == 0)
        {
            throw new ArgumentException($"The filter field '{key}' declares no operator.", nameof(operators));
        }

        // A mismatch between the operator and the column type is a programming error of the catalog, found at start-up, not at request time.
        foreach (var op in operators)
        {
            var supported = op switch
            {
                FilterOp.Eq or FilterOp.In => true,
                FilterOp.Range => Underlying != typeof(string) && Underlying != typeof(bool) && Underlying != typeof(Guid),
                FilterOp.Prefix => Underlying == typeof(string),
                FilterOp.IsNull => IsNullable,
                _ => false
            };
            if (!supported)
            {
                throw new ArgumentException($"The operator {op} cannot be used on the column of filter field '{key}' ({ValueType.Name}).", nameof(operators));
            }
        }

        if (normalizeText && Underlying != typeof(string))
        {
            throw new ArgumentException($"Only a string column can be a normalized text field ('{key}').", nameof(normalizeText));
        }

        Key = key;
        Title = title;
        Operators = operators.ToHashSet();
        _column = column;
        _normalizeText = normalizeText;
    }

    public string Key { get; }

    public string Title { get; }

    public IReadOnlySet<FilterOp> Operators { get; }

    public ChronoAuditResult<Expression<Func<TRow, bool>>> Build(FilterOp op, IReadOnlyList<string> values, FilterLimits limits, ITextNormalizer normalizer)
    {
        if (!Operators.Contains(op))
        {
            return ChronoAuditResult.Failure<Expression<Func<TRow, bool>>>(FilterErrors.OperatorNotAllowed(Title, op));
        }

        var body = _column.Body;
        Expression? predicate;
        switch (op)
        {
            case FilterOp.IsNull:
                if (values.Count != 0)
                {
                    return Fail("This operator takes no value.");
                }

                predicate = Expression.Equal(body, Expression.Constant(null, ValueType));
                break;

            case FilterOp.Eq:
                {
                    if (values.Count != 1 || !TryParse(values[0], normalizer, out var one))
                    {
                        return Fail(values.Count != 1 ? "Exactly one value is required." : "The value does not fit the field type.");
                    }

                    predicate = Expression.Equal(body, Parameterized(one));
                    break;
                }

            case FilterOp.In:
                {
                    if (values.Count is 0 || values.Count > limits.MaxInValues)
                    {
                        return Fail($"The number of values must be between 1 and {limits.MaxInValues}.");
                    }

                    var parsed = new TValue?[values.Count];
                    for (var i = 0; i < values.Count; i++)
                    {
                        if (!TryParse(values[i], normalizer, out var item))
                        {
                            return Fail("The value does not fit the field type.");
                        }

                        parsed[i] = item;
                    }

                    // A captured array: EF sends it as one parameter (one statement), whatever its length.
                    predicate = Expression.Call(ContainsMethod, Parameterized(parsed.Distinct().ToArray()), body);
                    break;
                }

            case FilterOp.Range:
                {
                    if (values.Count != 2 || (string.IsNullOrWhiteSpace(values[0]) && string.IsNullOrWhiteSpace(values[1])))
                    {
                        return Fail("Two bounds are required and at least one must have a value.");
                    }

                    TValue? low = default, high = default;
                    var hasLow = !string.IsNullOrWhiteSpace(values[0]);
                    var hasHigh = !string.IsNullOrWhiteSpace(values[1]);
                    if ((hasLow && !TryParse(values[0], normalizer, out low)) || (hasHigh && !TryParse(values[1], normalizer, out high)))
                    {
                        return Fail("The value does not fit the field type.");
                    }

                    if (hasLow && hasHigh && Comparer<TValue?>.Default.Compare(low, high) > 0)
                    {
                        return Fail("The lower bound is greater than the upper bound.");
                    }

                    predicate = (hasLow, hasHigh) switch
                    {
                        (true, true) => Expression.AndAlso(Expression.GreaterThanOrEqual(body, Parameterized(low)), Expression.LessThanOrEqual(body, Parameterized(high))),
                        (true, false) => Expression.GreaterThanOrEqual(body, Parameterized(low)),
                        _ => Expression.LessThanOrEqual(body, Parameterized(high))
                    };
                    break;
                }

            case FilterOp.Prefix:
                {
                    var text = values.Count == 1 ? Clean(values[0], normalizer) : null;
                    if (string.IsNullOrEmpty(text) || text.Length > FilterLimits.MaxPrefixLength)
                    {
                        return Fail($"Exactly one non-empty value of at most {FilterLimits.MaxPrefixLength} characters is required.");
                    }

                    // Null-safe, so the same expression is also correct when it is run in memory (SQL treats NULL LIKE as unknown anyway).
                    predicate = Expression.AndAlso(
                        Expression.NotEqual(body, Expression.Constant(null, ValueType)),
                        Expression.Call(body, StartsWithMethod, Expression.Property(Expression.Constant(new Box<string>(text)), nameof(Box<string>.Value))));
                    break;
                }

            default:
                return ChronoAuditResult.Failure<Expression<Func<TRow, bool>>>(FilterErrors.OperatorNotAllowed(Title, op));
        }

        return ChronoAuditResult.Success(Expression.Lambda<Func<TRow, bool>>(predicate, _column.Parameters));
    }

    private ChronoAuditResult<Expression<Func<TRow, bool>>> Fail(string reason) => ChronoAuditResult.Failure<Expression<Func<TRow, bool>>>(FilterErrors.InvalidValue(Title, reason));

    /// <summary>The value is read from a property of a constant box so EF turns it into a parameter (one cached plan per shape), not into a literal.</summary>
    private static MemberExpression Parameterized<T>(T? value) => Expression.Property(Expression.Constant(new Box<T?>(value)), nameof(Box<T>.Value));

    private string? Clean(string text, ITextNormalizer normalizer) => _normalizeText ? normalizer.Normalize(text.Trim()) : text.Trim();

    private bool TryParse(string text, ITextNormalizer normalizer, out TValue? value)
    {
        value = default;
        var raw = text?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        object? parsed;
        if (Underlying == typeof(string))
        {
            parsed = Clean(raw, normalizer);
        }
        else if (Underlying == typeof(Guid))
        {
            parsed = Guid.TryParse(normalizer.Normalize(raw), out var guid) ? guid : null;
        }
        else if (Underlying == typeof(bool))
        {
            parsed = bool.TryParse(raw, out var flag) ? flag : null;
        }
        else if (Underlying == typeof(DateTimeOffset))
        {
            parsed = DateTimeOffset.TryParse(normalizer.Normalize(raw), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
        }
        else if (Underlying.IsEnum)
        {
            // Names only: a bare number would be accepted by Enum.TryParse even when it is not a defined member.
            parsed = !char.IsDigit(raw[0]) && Enum.TryParse(Underlying, raw, ignoreCase: true, out var member) && Enum.IsDefined(Underlying, member!) ? member : null;
        }
        else
        {
            var digits = normalizer.Normalize(raw);
            parsed = Type.GetTypeCode(Underlying) switch
            {
                TypeCode.Int32 => int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
                TypeCode.Int64 => long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
                TypeCode.Int16 => short.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
                TypeCode.Byte => byte.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null,
                TypeCode.Decimal => decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null,
                _ => throw new NotSupportedException($"The filter field type {Underlying.Name} is not supported.")
            };
        }

        if (parsed is null)
        {
            return false;
        }

        value = (TValue)parsed;
        return true;
    }

    /// <summary>A holder whose property EF reads as a query parameter.</summary>
    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }
}
