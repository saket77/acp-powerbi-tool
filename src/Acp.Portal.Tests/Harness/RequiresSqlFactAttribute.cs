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

/// <summary>
/// A test that needs SQL Server <i>and</i> Azurite — the staged upload path writes a real blob
/// and a real queue message, because a fake of either would prove nothing about the pipeline.
/// </summary>
public sealed class RequiresLocalServicesFactAttribute : FactAttribute
{
    public RequiresLocalServicesFactAttribute()
    {
        if (!SqlServerFixture.IsAvailable || !LocalStorage.IsAvailable)
        {
            Skip = "SQL Server or Azurite is not running. Start them with scripts/dev-up.sh.";
        }
    }
}

/// <inheritdoc cref="RequiresLocalServicesFactAttribute"/>
public sealed class RequiresLocalServicesTheoryAttribute : TheoryAttribute
{
    public RequiresLocalServicesTheoryAttribute()
    {
        if (!SqlServerFixture.IsAvailable || !LocalStorage.IsAvailable)
        {
            Skip = "SQL Server or Azurite is not running. Start them with scripts/dev-up.sh.";
        }
    }
}

/// <summary>Azurite, from docker-compose.yml.</summary>
internal static class LocalStorage
{
    public static bool IsAvailable { get; } = CanReach();

    private static bool CanReach()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            // Unauthenticated, so a 400 is as good an answer as a 200 — it means something is
            // listening and speaking the Blob protocol.
            var response = http.GetAsync("http://127.0.0.1:10000/devstoreaccount1?comp=list")
                .GetAwaiter().GetResult();

            return response.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception)
        {
            return false;
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
