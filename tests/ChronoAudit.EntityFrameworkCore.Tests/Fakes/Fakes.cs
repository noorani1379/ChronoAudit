using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>An actor with settable properties and every optional member overridden.</summary>
public sealed class FakeAuditActor : IAuditActor
{
    public string? UserId { get; set; } = "user-1";

    public string? DisplayName { get; set; } = "Ada";

    public string? UserIp { get; set; } = "10.0.0.7";

    public string? SessionId { get; set; } = "sess-1";

    public string? Role { get; set; }

    public string? RoleTitle { get; set; }

    public string? ActorUserId { get; set; }

    public bool IsDelegated { get; set; }

    public object? AuditMetadata { get; set; }
}

/// <summary>Implements only the two required members, so the default interface members are exercised (trap T6).</summary>
public sealed class MinimalAuditActor : IAuditActor
{
    public string? UserId => "min-user";

    public string? DisplayName => "Minimal";
}

public sealed class FakeCorrelation(Guid id) : ICorrelationContext
{
    public Guid CorrelationId { get; } = id;
}

/// <summary>A clock with a settable now.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A dictionary-backed configuration, so tests need no configuration package.</summary>
public sealed class FakeConfiguration(Dictionary<string, string?> values, string prefix = "") : IConfigurationSection
{
    public string? this[string key]
    {
        get => values.GetValueOrDefault(Full(key));
        set => values[Full(key)] = value;
    }

    public string Key => prefix.Split(':').Last();

    public string Path => prefix;

    public string? Value
    {
        get => values.GetValueOrDefault(prefix);
        set => values[prefix] = value;
    }

    public IConfigurationSection GetSection(string key) => new FakeConfiguration(values, Full(key));

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => throw new NotSupportedException();

    private string Full(string key) => prefix.Length == 0 ? key : prefix + ":" + key;
}

public sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "Tests";

    public string ContentRootPath { get; set; } = ".";

    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

/// <summary>Collects every log entry, so tests can assert what was logged (and what was not).</summary>
public sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    public sealed record LogEntry(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message);

    private sealed class Logger(CapturingLoggerProvider owner, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
            {
                owner._entries.Add(new LogEntry(logLevel, category, formatter(state, exception)));
            }
        }
    }
}
