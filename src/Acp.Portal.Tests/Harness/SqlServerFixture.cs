using Acp.Portal.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Tests.Harness;

/// <summary>
/// A database of its own, built from the real migrations — row-level security policy included.
/// </summary>
/// <remarks>
/// Deliberately not the development database. These tests drop and recreate it, and doing that
/// to the database somebody is clicking around in would be rude; keeping them apart also means
/// a failure here is never explained away by leftover data.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Server =
        "Server=localhost,1433;User Id=sa;Password=Acp_Local_Dev_2026!;"
        + "TrustServerCertificate=True;Encrypt=False;Connect Timeout=5";

    private const string DatabaseName = "AcpPortal_Tests";

    public string ConnectionString { get; } = $"{Server};Database={DatabaseName}";

    public static bool IsAvailable { get; } = SqlProbe.CanConnect($"{Server};Database=master");

    public async Task InitializeAsync()
    {
        if (!IsAvailable) return;

        await using (var master = new SqlConnection($"{Server};Database=master"))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();

            // SINGLE_USER WITH ROLLBACK IMMEDIATE because a previous run's pooled connections
            // may still be attached, and a DROP would otherwise block forever.
            command.CommandText =
                $"""
                 IF DB_ID('{DatabaseName}') IS NOT NULL
                 BEGIN
                     ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                     DROP DATABASE [{DatabaseName}];
                 END
                 CREATE DATABASE [{DatabaseName}];
                 """;
            await command.ExecuteNonQueryAsync();
        }

        // No interceptor here: migrations are DDL and need no session context. The policy they
        // create is what everything after this is measured against.
        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlServer(ConnectionString, sql => sql.MigrationsAssembly(typeof(PortalDbContext).Assembly.FullName))
            .Options;

        await using var db = new PortalDbContext(options);
        await db.Database.MigrateAsync();
    }

    /// <summary>Runs a query outside the application entirely, as whoever you say.</summary>
    /// <remarks>
    /// This is how the suite shows that SQL refuses on its own rather than because some LINQ
    /// happened to carry the right <c>Where</c>. There is no EF here, no services, no filters —
    /// only a connection, a session context, and whatever the database decides to return.
    /// </remarks>
    public async Task<T> AsUserAsync<T>(Guid? userId, Func<SqlConnection, Task<T>> work)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText =
                "EXEC sp_set_session_context @key = N'UserId', @value = @userId, @read_only = 1;";
            stamp.Parameters.Add(new SqlParameter("@userId", System.Data.SqlDbType.UniqueIdentifier)
            {
                Value = (object?)userId ?? DBNull.Value,
            });
            await stamp.ExecuteNonQueryAsync();
        }

        return await work(connection);
    }

    /// <summary>Reads past the policy, the way the import worker will. Used only to set tests up.</summary>
    public async Task<T> AsUnfilteredAsync<T>(Func<SqlConnection, Task<T>> work)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText =
                "EXEC sp_set_session_context @key = N'Unfiltered', @value = 1, @read_only = 1;";
            await stamp.ExecuteNonQueryAsync();
        }

        return await work(connection);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
