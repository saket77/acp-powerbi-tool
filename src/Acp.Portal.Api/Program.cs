using System.Text.Json;
using Acp.Portal.Api.Auth;
using Acp.Portal.Api.Endpoints;
using Acp.Portal.Infrastructure;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // The one file you fill in after working through scripts/tenant-setup.md. Git-ignored, and
    // read only here: a deployed environment has no such file and gets these values from app
    // settings and its managed identity instead.
    builder.Configuration.AddJsonFile(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "config", "entra.local.json"),
        optional: true,
        reloadOnChange: true);
}

var authMode = builder.Configuration
    .GetSection(PortalAuthOptions.SectionName)
    .GetValue(typeof(AuthMode), "Mode") as AuthMode? ?? AuthMode.Development;

builder.Services.AddPortalInfrastructure(builder.Configuration);
builder.Services.AddPortalIdentity(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddPortalAuthentication(builder.Configuration, builder.Environment);

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

if (!app.Environment.IsDevelopment())
{
    // The session cookie is marked Secure outside development, so a request that arrived over
    // plain http would silently carry no session at all. Redirect rather than puzzle.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

if (authMode == AuthMode.Development)
{
    app.Logger.LogWarning(
        "Development sign-in is on. Anyone can sign in as any account in Auth:Development:Accounts, "
        + "and no password is checked. Set Auth:Mode to Entra once config/entra.local.json is filled in.");
}

// Liveness: the process is up. Used by the platform, not by people.
app.MapHealthChecks("/api/health/live", new()
{
    Predicate = _ => false,
});

// Readiness: the process is up and the database answers.
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
    phase = 2,
    // Phase 2 adds identity: sign-in, the session cookie, and inviting client contacts.
    // No Power BI and no grants yet. docs/implementation-plan.md tracks what each phase adds.
    environment = app.Environment.EnvironmentName,
    authMode = authMode.ToString(),
})).AllowAnonymous();

app.MapAuthEndpoints(authMode);
app.MapMeEndpoint(authMode);
app.MapInvitationEndpoints();

app.Run();

/// <summary>Exposed so the test project can drive the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;
