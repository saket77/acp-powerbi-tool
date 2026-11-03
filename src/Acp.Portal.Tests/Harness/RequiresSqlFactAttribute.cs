using Microsoft.Data.SqlClient;

namespace Acp.Portal.Tests.Harness;

/// <summary>
/// A test that needs the real SQL Server from docker-compose, and says so when it is not there.
/// </summary>
/// <remarks>
/// Row-level security is a database feature. Proving it against an in-memory provider would
/// prove nothing at all, so these tests insist on the real thing — but a developer without the
/// containers running should get a clear skip rather than a wall of connection failures, and
/// certainly not a green run that checked nothing.
/// </remarks>
public sealed class RequiresSqlFactAttribute : FactAttribute
{
    public RequiresSqlFactAttribute()
    {
        if (!SqlServerFixture.IsAvailable)
        {
            Skip = "SQL Server is not running. Start it with scripts/dev-up.sh.";
        }
    }
}

/// <inheritdoc cref="RequiresSqlFactAttribute"/>
public sealed class RequiresSqlTheoryAttribute : TheoryAttribute
{
    public RequiresSqlTheoryAttribute()
    {
        if (!SqlServerFixture.IsAvailable)
        {
            Skip = "SQL Server is not running. Start it with scripts/dev-up.sh.";
        }
    }
}

internal static class SqlProbe
{
    public static bool CanConnect(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
