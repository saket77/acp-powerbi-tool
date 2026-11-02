using System.Text.Json;
using Acp.Portal.Infrastructure;
using Acp.Portal.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPortalInfrastructure(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<PortalDbContext>(
        name: "database",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness: the process is up. Used by the platform, not by people.
app.MapHealthChecks("/api/health/live", new()
{
    Predicate = _ => false,
});

// Readiness: the process is up and the database answers. This is the one Phase 1 proves.
app.MapHealthChecks("/api/health", new()
{
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), error = e.Value.Exception?.Message }),
        }));
    },
});

app.MapGet("/api/version", () => Results.Ok(new
{
    name = "ACP Databook Portal",
    phase = 1,
    // Phase 1 is the skeleton: no identity, no Power BI, no grants yet.
    // docs/implementation-plan.md tracks what each phase adds.
    environment = app.Environment.EnvironmentName,
}));

app.Run();

/// <summary>Exposed so the test project can drive the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;
