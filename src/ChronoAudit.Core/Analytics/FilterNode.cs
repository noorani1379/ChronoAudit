
namespace ChronoAudit;

/// <summary>The closed list of operators a filter may use.</summary>
public enum FilterOp
{
    /// <summary>Equal to exactly one value.</summary>
    Eq = 1,

    /// <summary>One of 1..<see cref="FilterLimits.MaxInValues"/> values.</summary>
    In = 2,

    /// <summary>Between two bounds, both inclusive; an empty bound is open (at least one must be given).</summary>
    Range = 3,

    /// <summary>Text that starts with the value (translated to an index-friendly <c>StartsWith</c>, never <c>Contains</c>).</summary>
    Prefix = 4,

    /// <summary>The column is null.</summary>
    IsNull = 5
}

/// <summary>
/// One node of a filter: exactly one of a condition (<see cref="Field"/> + <see cref="Op"/> + <see cref="Values"/>), an OR group
/// (<see cref="Or"/>) or an AND group (<see cref="And"/>). The root is an implicit AND of its nodes. Allowed shape (depth 3, G0-Q7):
/// root -> (condition | OR group); OR member -> (condition | AND group); AND member -> condition. A plain record on purpose: it binds from
/// JSON without custom converters and the shape is checked by <see cref="FilterBuilder{TRow}"/>, not by the type.
/// </summary>
public sealed record FilterNode(
    string? Field = null, FilterOp? Op = null, IReadOnlyList<string>? Values = null, IReadOnlyList<FilterNode>? Or = null, IReadOnlyList<FilterNode>? And = null)
{
    /// <summary>A condition on one field.</summary>
    /// <param name="field">The filter field key.</param>
    /// <param name="op">The operator.</param>
    /// <param name="values">The operator values.</param>
    /// <returns>The node.</returns>
    public static FilterNode Condition(string field, FilterOp op, params string[] values) => new(Field: field, Op: op, Values: values);

    /// <summary>An OR group.</summary>
    /// <param name="members">The members.</param>
    /// <returns>The node.</returns>
    public static FilterNode AnyOf(params FilterNode[] members) => new(Or: members);

    /// <summary>An AND group.</summary>
    /// <param name="conditions">The members.</param>
    /// <returns>The node.</returns>
    public static FilterNode AllOf(params FilterNode[] conditions) => new(And: conditions);
}

/// <summary>The caps that bound a filter before any expression is built.</summary>
/// <param name="MaxConditions">Conditions in the whole tree.</param>
/// <param name="MaxOrMembers">Members of one OR group, and conditions of one AND group.</param>
/// <param name="MaxInValues">Values of one <see cref="FilterOp.In"/>.</param>
public sealed record FilterLimits(int MaxConditions = 20, int MaxOrMembers = 10, int MaxInValues = 50)
{
    /// <summary>The max prefix length.</summary>
    public const int MaxPrefixLength = 64;

    /// <summary>The default.</summary>
    public static readonly FilterLimits Default = new();
}

/// <summary>The one error code of a refused filter.</summary>
public static class FilterErrors
{
    /// <summary>The code.</summary>
    public const string Code = "Filter.NotAllowed";

    /// <summary>The not allowed.</summary>
    public static ChronoAuditError NotAllowed(string name) => ChronoAuditError.Validation(Code, $"The filter or sort '{name}' is not allowed for this report.");

    /// <summary>The operator not allowed.</summary>
    public static ChronoAuditError OperatorNotAllowed(string field, FilterOp op) => ChronoAuditError.Validation(Code, $"The operator '{op}' is not allowed for the filter '{field}'.");

    /// <summary>The invalid value.</summary>
    public static ChronoAuditError InvalidValue(string field, string reason) => ChronoAuditError.Validation(Code, $"The value of the filter '{field}' is not valid: {reason}");

    /// <summary>The shape.</summary>
    public static ChronoAuditError Shape(string reason) => ChronoAuditError.Validation(Code, $"The filter structure is not valid: {reason}");
}
