using Acp.Portal.Domain.Abstractions;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// The Databook template contract, as a function of a model's shape.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure: a model description in, a list of failures out, no network and no clock.
/// That is what makes the contract testable without a capacity, and it is the half of the checks
/// most likely to need changing when the template moves to 1.7.
/// </para>
/// <para>
/// The messages are written for a consultant who has just had an upload refused and wants to
/// know what to change in Power BI Desktop. Not one of them names a class, an API or a column id.
/// </para>
/// </remarks>
public static class TemplateChecks
{
    public static IReadOnlyList<TemplateCheckFailure> Run(
        ModelDescription model,
        IReadOnlyList<string> expectedCompanyCodes,
        PowerBiOptions options)
    {
        var failures = new List<TemplateCheckFailure>();

        CheckRoles(model, options, failures);
        CheckTemplateVersion(model, options, failures);
        CheckCompaniesTable(model, options, failures);
        CheckCompanyCodes(model, expectedCompanyCodes, failures);
        CheckEverythingIsFiltered(model, options, failures);

        return failures;
    }

    /// <summary>One message, for the version row. The first failure is the one worth fixing first.</summary>
    public static string Summarise(IReadOnlyList<TemplateCheckFailure> failures) =>
        failures.Count switch
        {
            0 => "",
            1 => failures[0].Message,
            _ => $"{failures[0].Message} ({failures.Count - 1} more to fix as well.)",
        };

    private static void CheckRoles(
        ModelDescription model, PowerBiOptions options, List<TemplateCheckFailure> failures)
    {
        foreach (var role in new[] { options.ClientRole, options.AcpRole })
        {
            if (!model.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add(new TemplateCheckFailure(
                    $"role.{role}",
                    $"This file has no {role} role. Use template {options.MinimumTemplateVersion:0.0}."));
            }
        }
    }

    private static void CheckTemplateVersion(
        ModelDescription model, PowerBiOptions options, List<TemplateCheckFailure> failures)
    {
        if (model.TemplateVersion is null)
        {
            failures.Add(new TemplateCheckFailure(
                "template.version.missing",
                $"This file has no '{options.TemplateVersionMeasure}' measure, so it was not built "
                + $"from the ACP template. Start again from template {options.MinimumTemplateVersion:0.0}."));
            return;
        }

        if (model.TemplateVersion < options.MinimumTemplateVersion)
        {
            failures.Add(new TemplateCheckFailure(
                "template.version.old",
                $"This file is built on template {model.TemplateVersion:0.0}. "
                + $"Rebuild it on {options.MinimumTemplateVersion:0.0} or newer."));
        }
    }

    private static void CheckCompaniesTable(
        ModelDescription model, PowerBiOptions options, List<TemplateCheckFailure> failures)
    {
        var companies = model.Tables
            .FirstOrDefault(t => t.Name.Equals(options.CompaniesTable, StringComparison.OrdinalIgnoreCase));

        if (companies is null)
        {
            failures.Add(new TemplateCheckFailure(
                "companies.table",
                $"This file has no '{options.CompaniesTable}' table. The portal filters clients by "
                + "company, and without that table it cannot."));
            return;
        }

        if (!companies.Columns.Contains(options.CompanyCodeColumn, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(new TemplateCheckFailure(
                "companies.column",
                $"The '{options.CompaniesTable}' table has no '{options.CompanyCodeColumn}' column. "
                + "That column is what a client's access is matched against."));
        }
    }

    private static void CheckCompanyCodes(
        ModelDescription model, IReadOnlyList<string> expected, List<TemplateCheckFailure> failures)
    {
        if (model.CompanyCodes.Count == 0)
        {
            failures.Add(new TemplateCheckFailure(
                "companies.empty",
                "The companies table in this file is empty, so every client would see nothing. "
                + "Refresh the Databook before uploading it."));
            return;
        }

        var inModel = model.CompanyCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onProject = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Codes in the model that the project has never heard of are the dangerous direction:
        // a client granted "everything on this project" would be shown a company nobody decided
        // they should see.
        var unexpected = inModel.Except(onProject, StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (unexpected.Count > 0)
        {
            failures.Add(new TemplateCheckFailure(
                "companies.unexpected",
                $"This file contains data for {Join(unexpected)}, which {(unexpected.Count == 1 ? "is" : "are")} "
                + "not on this project. Either add the company to the client, or refresh the Databook "
                + "with the right scope."));
        }

        // The other direction is a mistake rather than a risk: somebody would be granted a
        // company and then see an empty report.
        var missing = onProject.Except(inModel, StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (missing.Count > 0)
        {
            failures.Add(new TemplateCheckFailure(
                "companies.missing",
                $"This project has {Join(missing)}, but this file has no data for "
                + $"{(missing.Count == 1 ? "it" : "them")}. Anyone granted "
                + $"{(missing.Count == 1 ? "that company" : "those companies")} would see an empty report."));
        }
    }

    private static void CheckEverythingIsFiltered(
        ModelDescription model, PowerBiOptions options, List<TemplateCheckFailure> failures)
    {
        var unfiltered = model.Tables
            .Where(t => !t.FilteredByClientRole && !IsAcpOnly(t.Name, options))
            .Select(t => t.Name)
            .Order()
            .ToList();

        if (unfiltered.Count > 0)
        {
            failures.Add(new TemplateCheckFailure(
                "rls.unfiltered",
                $"The {options.ClientRole} role does not filter {Join(unfiltered)}, so every client "
                + $"would see all of {(unfiltered.Count == 1 ? "that table" : "those tables")}. "
                + $"Add a filter in Power BI Desktop, or rename {(unfiltered.Count == 1 ? "it" : "them")} "
                + $"to start with '{options.AcpOnlyTablePrefixes.FirstOrDefault() ?? "ACP"}' if "
                + $"{(unfiltered.Count == 1 ? "it is" : "they are")} for ACP only."));
        }
    }

    private static bool IsAcpOnly(string tableName, PowerBiOptions options) =>
        options.AcpOnlyTablePrefixes.Any(p => tableName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string Join(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => $"'{values[0]}'",
            2 => $"'{values[0]}' and '{values[1]}'",
            _ => string.Join(", ", values.Take(values.Count - 1).Select(v => $"'{v}'"))
                 + $" and '{values[^1]}'",
        };
}

/// <param name="Check">Stable key for the audit log and the health page. Never shown to a consultant.</param>
/// <param name="Message">What to change in Power BI Desktop, in a sentence.</param>
public sealed record TemplateCheckFailure(string Check, string Message);
