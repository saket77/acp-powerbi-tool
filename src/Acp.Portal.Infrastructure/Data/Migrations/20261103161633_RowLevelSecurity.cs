using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acp.Portal.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Isolation wall 2: the database returns only the projects the session's user holds, even
    /// if the application forgets a filter.
    /// </summary>
    /// <remarks>
    /// The predicate is uniform — a row is visible if and only if you hold a live grant on it —
    /// rather than "clients are filtered, staff are not". Staff hold grants too, so the rule
    /// needs no exception, and an exception is exactly the thing that would later be widened by
    /// someone in a hurry.
    ///
    /// Two deliberate omissions:
    ///
    /// * No block predicate on INSERT into Projects. Creating a project writes the project and
    ///   the creator's Lead grant in that order, so at the moment of the insert no grant exists
    ///   yet. A block predicate there would make it impossible to create a project at all.
    /// * ProjectGrants itself is not filtered. The predicate reads it, so filtering it would
    ///   recurse. Nothing exposes grants to clients; that stays an application rule.
    ///
    /// Note for whoever writes the next data migration: migrations run with no session context,
    /// so Projects and ReportVersions look empty to them. DDL is unaffected — a migration that
    /// needs to touch rows must open an UnfilteredScope or set the context itself.
    /// </remarks>
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA rls;");

            // SCHEMABINDING is required for a security predicate: it stops anyone dropping the
            // tables this depends on out from under it.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION rls.fn_ProjectVisible(@ProjectId uniqueidentifier)
                RETURNS TABLE
                WITH SCHEMABINDING
                AS
                RETURN
                    SELECT 1 AS visible
                    WHERE
                        -- Work with no user behind it: the import worker, test fixtures.
                        TRY_CAST(SESSION_CONTEXT(N'Unfiltered') AS bit) = 1
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.ProjectGrants AS g
                            WHERE g.ProjectId = @ProjectId
                              AND g.UserId = TRY_CAST(SESSION_CONTEXT(N'UserId') AS uniqueidentifier)
                              -- An expired grant stops working here, not only in the API.
                              AND (g.EndDate IS NULL OR g.EndDate >= CAST(SYSUTCDATETIME() AS date)));
                """);

            migrationBuilder.Sql(
                """
                CREATE SECURITY POLICY rls.ProjectAccess
                    ADD FILTER PREDICATE rls.fn_ProjectVisible(Id)        ON dbo.Projects,
                    ADD FILTER PREDICATE rls.fn_ProjectVisible(ProjectId) ON dbo.ReportVersions,
                    ADD BLOCK PREDICATE  rls.fn_ProjectVisible(ProjectId) ON dbo.ReportVersions AFTER INSERT,
                    ADD BLOCK PREDICATE  rls.fn_ProjectVisible(ProjectId) ON dbo.ReportVersions AFTER UPDATE
                WITH (STATE = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SECURITY POLICY rls.ProjectAccess;");
            migrationBuilder.Sql("DROP FUNCTION rls.fn_ProjectVisible;");
            migrationBuilder.Sql("DROP SCHEMA rls;");
        }
    }
}
