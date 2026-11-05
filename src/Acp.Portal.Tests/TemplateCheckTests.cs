using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Infrastructure.PowerBi;

namespace Acp.Portal.Tests;

/// <summary>
/// The Databook template contract, checked without a capacity, a model or a network.
/// </summary>
/// <remarks>
/// The messages are asserted as well as the failures, because the message is the product here.
/// A consultant whose upload was refused needs to know what to change in Power BI Desktop, and
/// "rls.unfiltered" tells them nothing.
/// </remarks>
public sealed class TemplateCheckTests
{
    private static readonly PowerBiOptions Options = new();

    private static ModelDescription Good(params string[] codes) => new(
        Roles: ["Client", "ACP"],
        Tables:
        [
            new("Companies", ["Ges.", "Name"], FilteredByClientRole: true),
            new("Financials", ["Company", "Amount"], FilteredByClientRole: true),
            new("ACP Margins", ["Margin"], FilteredByClientRole: false),
        ],
        TemplateVersion: 1.6m,
        CompanyCodes: codes.Length == 0 ? ["M01", "M02"] : codes);

    [Fact]
    public void AModelThatMeetsTheContractPasses()
    {
        Assert.Empty(TemplateChecks.Run(Good(), ["M01", "M02"], Options));
    }

    [Fact]
    public void AMissingClientRoleIsNamedInTheWireframesOwnWords()
    {
        var model = Good() with { Roles = ["ACP"] };

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Equal("role.Client", failure.Check);
        Assert.Equal("This file has no Client role. Use template 1.6.", failure.Message);
    }

    [Fact]
    public void AnOldTemplateSaysWhichVersionItIsAndWhichItNeeds()
    {
        var model = Good() with { TemplateVersion = 1.4m };

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Contains("1.4", failure.Message);
        Assert.Contains("1.6", failure.Message);
    }

    [Fact]
    public void NoTemplateVersionMeasureMeansItIsNotAnAcpFile()
    {
        var model = Good() with { TemplateVersion = null };

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Contains("not built from the ACP template", failure.Message);
    }

    [Fact]
    public void AMissingCompaniesTableExplainsWhyItMatters()
    {
        var model = Good() with
        {
            Tables = [new("Financials", ["Amount"], FilteredByClientRole: true)],
        };

        var failures = TemplateChecks.Run(model, ["M01"], Options);

        Assert.Contains(failures, f => f.Check == "companies.table");
        Assert.Contains(failures, f => f.Message.Contains("filters clients by company"));
    }

    [Fact]
    public void ACompaniesTableWithoutTheCodeColumnIsCaught()
    {
        var model = Good() with
        {
            Tables = [new("Companies", ["Name"], FilteredByClientRole: true)],
        };

        Assert.Contains(TemplateChecks.Run(model, ["M01", "M02"], Options), f => f.Check == "companies.column");
    }

    [Fact]
    public void DataForACompanyTheProjectDoesNotHaveIsRefused()
    {
        // The dangerous direction: somebody granted everything on this project would be shown
        // a company nobody decided they should see.
        var model = Good("M01", "M02", "ZZ99");

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Equal("companies.unexpected", failure.Check);
        Assert.Contains("'ZZ99'", failure.Message);
    }

    [Fact]
    public void AProjectCompanyMissingFromTheFileIsRefusedToo()
    {
        // The other direction is a mistake rather than a risk — somebody would be granted a
        // company and then open an empty report.
        var model = Good("M01");

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Equal("companies.missing", failure.Check);
        Assert.Contains("empty report", failure.Message);
    }

    [Fact]
    public void AnEmptyCompaniesTableIsCaughtBeforeAnythingElseAboutCodes()
    {
        var model = Good() with { CompanyCodes = [] };

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01"], Options));

        Assert.Equal("companies.empty", failure.Check);
        Assert.Contains("Refresh the Databook", failure.Message);
    }

    [Fact]
    public void AnUnfilteredTableIsTheFailureTheChecksExistFor()
    {
        var model = Good() with
        {
            Tables =
            [
                new("Companies", ["Ges."], FilteredByClientRole: true),
                new("Financials", ["Amount"], FilteredByClientRole: false),
            ],
        };

        var failure = Assert.Single(TemplateChecks.Run(model, ["M01", "M02"], Options));

        Assert.Equal("rls.unfiltered", failure.Check);
        Assert.Contains("every client would see all of that table", failure.Message);
    }

    [Fact]
    public void TablesNamedForAcpAreExpectedToBeUnfiltered()
    {
        // "ACP Margins" is unfiltered in every model here and never appears as a failure.
        Assert.Empty(TemplateChecks.Run(Good(), ["M01", "M02"], Options));
    }

    [Fact]
    public void TheSummaryLeadsWithOneFixAndCountsTheRest()
    {
        var model = Good("ZZ99") with { Roles = [], TemplateVersion = 1.0m };

        var failures = TemplateChecks.Run(model, ["M01"], Options);
        var summary = TemplateChecks.Summarise(failures);

        Assert.True(failures.Count > 1);
        Assert.StartsWith("This file has no Client role.", summary);
        Assert.Contains($"{failures.Count - 1} more to fix", summary);
    }

    [Fact]
    public void NothingWrongSummarisesToNothing()
    {
        Assert.Equal("", TemplateChecks.Summarise([]));
    }
}
