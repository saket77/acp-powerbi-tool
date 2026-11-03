using System.Data;
using System.Data.Common;
using Acp.Portal.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acp.Portal.Infrastructure.Data;

/// <summary>
/// Tells SQL Server who is asking, on every connection it opens. This is what makes the
/// row-level security predicates work.
/// </summary>
/// <remarks>
/// <para>
/// Two facts about <c>SESSION_CONTEXT</c> make this safe, both checked against SQL Server 2022
/// rather than recalled:
/// </para>
/// <list type="number">
/// <item>
/// A pooled connection handed back out has its session context <b>cleared</b> — the reset that
/// ADO.NET performs on reuse wipes it. So one request's identity cannot leak into the next,
/// even though it is physically the same connection.
/// </item>
/// <item>
/// Values can be written <c>@read_only = 1</c>, after which nothing in that session can change
/// them. Injected SQL cannot re-stamp the connection as somebody else — it would have to get a
/// new connection, which would be stamped as the same person again.
/// </item>
/// </list>
/// <para>
/// Writing read-only has a consequence worth keeping: stamping the same open connection twice
/// throws. That is on purpose. It would mean one connection served two identities without a
/// reset in between, which is precisely the bug this class exists to prevent, and it should be
/// loud rather than silently keeping whichever identity got there first.
/// </para>
/// </remarks>
public sealed class SessionContextInterceptor(ICurrentUserAccessor caller) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Build(DbConnection connection)
    {
        var current = caller.Current;
        var unfiltered = current.Unfiltered || UnfilteredScope.IsActive;

        var command = connection.CreateCommand();
        command.CommandText =
            """
            EXEC sp_set_session_context @key = N'UserId',     @value = @userId,     @read_only = 1;
            EXEC sp_set_session_context @key = N'Unfiltered', @value = @unfiltered, @read_only = 1;
            """;

        // Passed as a real uniqueidentifier, so the predicate's cast is a formality rather than
        // a parse of whatever happened to be in a string.
        Add(command, "@userId", SqlDbType.UniqueIdentifier, current.UserId);
        Add(command, "@unfiltered", SqlDbType.Bit, unfiltered);

        return command;
    }

    private static void Add(DbCommand command, string name, SqlDbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;

        if (parameter is Microsoft.Data.SqlClient.SqlParameter sql)
        {
            sql.SqlDbType = type;
        }

        command.Parameters.Add(parameter);
    }
}
