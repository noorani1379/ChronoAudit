namespace ChronoAudit.IntegrationTests;

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute(DatabaseProvider provider)
    {
        Provider = provider;
        if (provider == DatabaseProvider.SqlServer)
        {
            var conn = Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_SQLSERVER")
                ?? Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_SQLSERVER", EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(conn))
            {
                Skip = "CHRONOAUDIT_TEST_SQLSERVER environment variable is not set.";
            }
        }
        else if (provider == DatabaseProvider.PostgreSql)
        {
            var conn = Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_POSTGRES")
                ?? Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_POSTGRES", EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(conn))
            {
                Skip = "CHRONOAUDIT_TEST_POSTGRES environment variable is not set.";
            }
        }
    }

    public DatabaseProvider Provider { get; }
}
