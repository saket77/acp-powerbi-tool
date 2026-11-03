using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Acp.Portal.Tests;

public sealed class HealthEndpointTests : IDisposable
{
    private readonly PortalAppFactory _factory = new();

    [Fact]
    public async Task Health_ReportsHealthy_WhenTheDatabaseAnswers()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(
            "Healthy",
            body.GetProperty("checks").GetProperty("database").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Liveness_AnswersWithoutTouchingTheDatabase()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_NamesThePhase()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/version");

        Assert.Equal(3, body.GetProperty("phase").GetInt32());
    }

    public void Dispose() => _factory.Dispose();
}
