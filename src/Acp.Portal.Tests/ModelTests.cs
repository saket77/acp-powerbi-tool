using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Tests;

/// <summary>
/// Asserts the parts of the schema that carry a rule, rather than the schema in general.
/// These build the model against the SQL Server provider without opening a connection.
/// </summary>
public sealed class ModelTests
{
    private static PortalDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlServer("Server=never-dialled;Database=model-only")
            .Options;

        return new PortalDbContext(options);
    }

    [Fact]
    public void AtMostOneReleasedVersionPerProject_IsEnforcedByTheDatabase()
    {
        using var context = BuildContext();

        var index = context.Model
            .FindEntityType(typeof(ReportVersion))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_ReportVersions_OneReleasedPerProject");

        Assert.True(index.IsUnique);
        Assert.Equal("[State] = 'Released'", index.GetFilter());
    }

    [Fact]
    public void OneGrantPerPersonPerProject()
    {
        using var context = BuildContext();

        var entity = context.Model.FindEntityType(typeof(ProjectGrant))!;

        var index = entity.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ProjectId", "UserId" }));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void TheStableIdentityFromEntra_IsUnique()
    {
        using var context = BuildContext();

        var index = context.Model
            .FindEntityType(typeof(User))!
            .GetIndexes()
            .Single(i => i.Properties.Single().Name == nameof(User.EntraObjectId));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void CompanyCodesAreUniqueWithinAClient_NotGlobally()
    {
        using var context = BuildContext();

        var entity = context.Model.FindEntityType(typeof(Company))!;

        var index = entity.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ClientId", "Code" }));

        Assert.True(index.IsUnique);

        // Code alone must NOT be unique: two client groups may both use "DE01".
        Assert.DoesNotContain(
            entity.GetIndexes(),
            i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(Company.Code));
    }

    [Theory]
    [InlineData(typeof(Project), "Status")]
    [InlineData(typeof(User), "Kind")]
    [InlineData(typeof(ProjectGrant), "Role")]
    [InlineData(typeof(ReportVersion), "State")]
    [InlineData(typeof(UploadJob), "State")]
    public void EnumsAreStoredAsText_SoTheAuditLogReads(Type entity, string property)
    {
        using var context = BuildContext();

        var column = context.Model.FindEntityType(entity)!.FindProperty(property)!;

        Assert.Equal(typeof(string), column.GetProviderClrType());
    }
}
