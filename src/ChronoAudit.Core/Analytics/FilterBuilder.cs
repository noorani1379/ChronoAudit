using System.Linq.Expressions;

namespace ChronoAudit;

/// <summary>
/// Turns the filter of a request into ONE <c>Expression&lt;Func&lt;TRow,bool&gt;&gt;</c> from the catalog of a subject (G0-Q7). Pure: no I/O.
/// <list type="bullet">
/// <item>Only catalog keys and the closed operator list are accepted; a user string is never turned into a column or an expression.</item>
/// <item>Shape (depth 3): root AND -> (condition | OR) ; OR member -> (condition | AND) ; AND member -> condition. NOT and field-to-field comparison do not exist.</item>
/// <item>The caps (<see cref="FilterLimits"/>) are checked BEFORE any expression is built, so a hostile tree costs a counter, not memory.</item>
/// <item>Every refusal is a typed <c>Reporting.FilterNotAllowed</c> result that names the field, never an exception and never silently ignored.</item>
/// </list>
/// </summary>
public sealed class FilterBuilder<TRow>
{
    private readonly Dictionary<string, IFilterField<TRow>> _fields;
    private readonly FilterLimits _limits;
    private readonly ITextNormalizer _normalizer;

    /// <summary>The filter builder.</summary>
    public FilterBuilder(IEnumerable<IFilterField<TRow>> catalog, FilterLimits? limits = null, ITextNormalizer? normalizer = null)
    {
        _fields = catalog.ToDictionary(f => f.Key, StringComparer.Ordinal);
        _limits = limits ?? FilterLimits.Default;
        _normalizer = normalizer ?? IdentityTextNormalizer.Instance;
    }

    /// <summary>The build.</summary>
    public ChronoAuditResult<Expression<Func<TRow, bool>>> Build(IReadOnlyList<FilterNode>? filters)
    {
        var parameter = Expression.Parameter(typeof(TRow), "row");
        if (filters is null || filters.Count == 0)
        {
            return ChronoAuditResult.Success(Expression.Lambda<Func<TRow, bool>>(Expression.Constant(true), parameter));
        }

        var shape = CheckShapeAndCaps(filters);
        if (shape is not null)
        {
            return ChronoAuditResult.Failure<Expression<Func<TRow, bool>>>(shape);
        }

        Expression? all = null;
        foreach (var node in filters)
        {
            var built = BuildRootMember(node, parameter);
            if (built.IsFailure)
            {
                return ChronoAuditResult.Failure<Expression<Func<TRow, bool>>>(built.Error);
            }

            all = all is null ? built.Value : Expression.AndAlso(all, built.Value);
        }

        return ChronoAuditResult.Success(Expression.Lambda<Func<TRow, bool>>(all!, parameter));
    }

    // ---- shape and caps (no expression yet) ------------------------------------------------------------------------------------

    private ChronoAuditError? CheckShapeAndCaps(IReadOnlyList<FilterNode> root)
    {
        var conditions = 0;
        if (root.Count > _limits.MaxConditions)
        {
            return TooMany();
        }

        foreach (var node in root)
        {
            var kind = KindOf(node);
            if (kind is Kind.Invalid or Kind.And)
            {
                return Shape();
            }

            if (kind == Kind.Condition)
            {
                conditions++;
                continue;
            }

            if (node.Or!.Count == 0 || node.Or.Count > _limits.MaxOrMembers)
            {
                return TooManyMembers();
            }

            foreach (var member in node.Or)
            {
                var memberKind = KindOf(member);
                if (memberKind is Kind.Invalid or Kind.Or)
                {
                    return Shape();
                }

                if (memberKind == Kind.Condition)
                {
                    conditions++;
                }
                else
                {
                    if (member.And!.Count == 0 || member.And.Count > _limits.MaxOrMembers)
                    {
                        return TooManyMembers();
                    }

                    foreach (var leaf in member.And)
                    {
                        if (KindOf(leaf) != Kind.Condition)
                        {
                            return Shape();
                        }

                        conditions++;
                    }
                }

                if (conditions > _limits.MaxConditions)
                {
                    return TooMany();
                }
            }

            if (conditions > _limits.MaxConditions)
            {
                return TooMany();
            }
        }

        return conditions > _limits.MaxConditions ? TooMany() : null;
    }

    private enum Kind { Invalid, Condition, Or, And }

    private static Kind KindOf(FilterNode? node)
    {
        if (node is null)
        {
            return Kind.Invalid;
        }

        var condition = node.Field is not null || node.Op is not null || node.Values is not null;
        var parts = (condition ? 1 : 0) + (node.Or is not null ? 1 : 0) + (node.And is not null ? 1 : 0);
        if (parts != 1)
        {
            return Kind.Invalid;
        }

        if (node.Or is not null)
        {
            return Kind.Or;
        }

        if (node.And is not null)
        {
            return Kind.And;
        }

        return node.Field is not null && node.Op is not null ? Kind.Condition : Kind.Invalid;
    }

    private ChronoAuditError TooMany() => FilterErrors.Shape($"A filter may have at most {_limits.MaxConditions} conditions.");

    private ChronoAuditError TooManyMembers() => FilterErrors.Shape($"Each OR or AND group must have between 1 and {_limits.MaxOrMembers} members.");

    private static ChronoAuditError Shape() => FilterErrors.Shape(
        "At most three levels are allowed (AND, OR, AND); NOT, field-to-field comparison and empty or ambiguous nodes are not supported.");

    // ---- expression --------------------------------------------------------------------------------------------------------------

    private ChronoAuditResult<Expression> BuildRootMember(FilterNode node, ParameterExpression parameter)
        => KindOf(node) == Kind.Condition ? BuildCondition(node, parameter) : BuildAny(node.Or!, parameter);

    private ChronoAuditResult<Expression> BuildAny(IReadOnlyList<FilterNode> members, ParameterExpression parameter)
    {
        Expression? any = null;
        foreach (var member in members)
        {
            var built = KindOf(member) == Kind.Condition ? BuildCondition(member, parameter) : BuildAll(member.And!, parameter);
            if (built.IsFailure)
            {
                return built;
            }

            any = any is null ? built.Value : Expression.OrElse(any, built.Value);
        }

        return ChronoAuditResult.Success(any!);
    }

    private ChronoAuditResult<Expression> BuildAll(IReadOnlyList<FilterNode> conditions, ParameterExpression parameter)
    {
        Expression? all = null;
        foreach (var condition in conditions)
        {
            var built = BuildCondition(condition, parameter);
            if (built.IsFailure)
            {
                return built;
            }

            all = all is null ? built.Value : Expression.AndAlso(all, built.Value);
        }

        return ChronoAuditResult.Success(all!);
    }

    private ChronoAuditResult<Expression> BuildCondition(FilterNode node, ParameterExpression parameter)
    {
        if (!_fields.TryGetValue(node.Field!, out var field))
        {
            return ChronoAuditResult.Failure<Expression>(FilterErrors.NotAllowed(node.Field!));
        }

        var predicate = field.Build(node.Op!.Value, node.Values ?? [], _limits, _normalizer);
        if (predicate.IsFailure)
        {
            return ChronoAuditResult.Failure<Expression>(predicate.Error);
        }

        // Every field's lambda has its own parameter: re-bind it to the shared one so the whole tree is a single lambda.
        return ChronoAuditResult.Success(new Rebind(predicate.Value.Parameters[0], parameter).Visit(predicate.Value.Body));
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
