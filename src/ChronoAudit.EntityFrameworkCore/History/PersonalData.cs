using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Which fields of the audited tables are personal data. The lists are empty by default: the host names its own fields. Field names are
/// compared without regard to case.
/// </summary>
public sealed class PersonalDataOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:PersonalData";

    /// <summary>Fields masked as numbers (<see cref="PersonalDataMasker.MaskNumber(string?)"/>): ids, phone numbers.</summary>
    public ISet<string> NumberFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fields masked as text (<see cref="PersonalDataMasker.TextMask"/>): names, addresses, emails.</summary>
    public ISet<string> TextFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Decides whether a caller may see personal data unmasked. The host provides it; the default refuses every reveal.</summary>
public interface IPersonalDataRevealPolicy
{
    /// <summary>Tells whether the caller may reveal the personal data of a table.</summary>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="resourceType">The table.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the reveal is allowed.</returns>
    Task<bool> MayRevealAsync(ChronoAuditScope scope, string resourceType, CancellationToken cancellationToken);
}

internal sealed class RefuseAllRevealsPolicy : IPersonalDataRevealPolicy
{
    public Task<bool> MayRevealAsync(ChronoAuditScope scope, string resourceType, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Masks personal fields inside audit JSON and in field changes.</summary>
internal sealed class PersonalDataMasking(PersonalDataOptions options)
{
    private readonly HashSet<string> _numbers = new(options.NumberFields, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _texts = new(options.TextFields, StringComparer.OrdinalIgnoreCase);

    public bool IsPersonal(string field) => _numbers.Contains(field) || _texts.Contains(field);

    /// <summary>The JSON object with every personal field masked; with <paramref name="reveal"/> the text is returned as it is.</summary>
    public string MaskJson(string json, bool reveal)
    {
        if (reveal || (_numbers.Count == 0 && _texts.Count == 0))
        {
            return json;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return json;
            }

            foreach (var name in obj.Select(p => p.Key).ToList())
            {
                if (IsPersonal(name))
                {
                    obj[name] = MaskNode(name, obj[name]);
                }
            }

            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public JsonElement? Mask(string field, JsonElement? value)
    {
        if (value is null || !IsPersonal(field) || value.Value.ValueKind == JsonValueKind.Null)
        {
            return value;
        }

        var masked = MaskNode(field, JsonNode.Parse(value.Value.GetRawText()));
        return masked is null ? null : JsonDocument.Parse(masked.ToJsonString()).RootElement.Clone();
    }

    private JsonValue? MaskNode(string field, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var text = node.ToJsonString().Trim('"');
        return _numbers.Contains(field) ? JsonValue.Create(PersonalDataMasker.MaskNumber(text)) : JsonValue.Create(PersonalDataMasker.TextMask);
    }
}
