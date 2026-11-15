using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rules.Api.Demo;
using Rules.Domain;
using Xunit;

namespace Rules.Tests;

public sealed class HttpFlowTests
{
    [Fact]
    public async Task Http_publish_simulate_execute_history_and_retro_end_to_end()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Database"] = Path.Combine(Path.GetTempPath(), "zen-http", Guid.NewGuid().ToString("N"), "engine.db") })));
        using var client = factory.CreateClient();
        var health = await client.GetAsync("/api/health"); health.EnsureSuccessStatusCode();
        Assert.Contains("GoRules.ZenEngine/2.1.8", await health.Content.ReadAsStringAsync());
        var initialized = await client.PostAsJsonAsync("/api/demo/initialize", new { }); initialized.EnsureSuccessStatusCode();
        var simulation = await client.PostAsJsonAsync("/api/calculations/simulate", DemoData.Event()); simulation.EnsureSuccessStatusCode();
        var historyBefore = await client.GetStringAsync("/api/history"); Assert.Contains("\"runs\":[]", historyBefore);
        var executed = await client.PostAsJsonAsync("/api/calculations/execute", DemoData.Event()); executed.EnsureSuccessStatusCode();
        var result = await executed.Content.ReadFromJsonAsync<CalculationResult>(); Assert.Equal(7226.32m, result!.Total);
        var replay = await client.GetFromJsonAsync<CalculationResult>("/api/runs/" + result.RunId); Assert.Equal(result.Total, replay!.Total);
        using var blocked = new HttpRequestMessage(HttpMethod.Post, "/api/calculations/execute") { Content = JsonContent.Create(DemoData.Event()) };
        blocked.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(blocked)).StatusCode);
    }
}
