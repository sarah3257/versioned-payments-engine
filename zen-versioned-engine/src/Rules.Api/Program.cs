using System.Net;
using System.Text.Json;
using Rules.Api.Demo;
using Rules.Application;
using Rules.Application.Execution;
using Rules.Application.Rules;
using Rules.Application.Publication;
using Rules.Domain;
using Rules.Infrastructure.Persistence;
using Rules.Infrastructure.Rules;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 2_000_000);
builder.Services.ConfigureHttpJsonOptions(x => { x.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; });
builder.Services.AddSingleton<IRuleRuntime, ZenRuleRuntime>();
builder.Services.AddSingleton<IEngineStore>(_ => new SqliteEngineStore(builder.Configuration["Engine:Database"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "engine.db")));
builder.Services.AddSingleton<ConditionEvaluator>(); builder.Services.AddSingleton<ComponentExecutor>();
builder.Services.AddSingleton<DecisionTableAnalyzer>();
builder.Services.AddSingleton<CalculationRunner>(); builder.Services.AddSingleton<EngineService>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'";
    // Local POC only. Never expose this demo without replacing this gate with organization authentication.
    var ip = context.Connection.RemoteIpAddress;
    if (ip is not null && !IPAddress.IsLoopback(ip)) { context.Response.StatusCode = 403; return; }
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]")) { context.Response.StatusCode = 403; return; }
    if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value))
    { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (BusinessException e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { code = e.Code, message = e.Message }); }
    catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "JSON", message = "תוכן JSON אינו תקין או מכיל שדות לא מוכרים" }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/health", (IRuleRuntime runtime) => new { status = "ok", engine = CalculationRunner.EngineVersion, rules = runtime.Version, mode = "Local POC — synthetic data" });
app.MapGet("/api/catalog", (EngineService engine) => { var s = engine.State(); return new { s.Templates, s.Components, s.Models, s.References, s.Agreements, s.Publications, hash = Rules.Domain.Json.CatalogHash(s) }; });
app.MapGet("/api/history", (EngineService engine) => { var s = engine.State(); return new { s.Positions, s.Ledger, s.Settlements, s.Audit, s.Failures, runs = s.Runs.Select(x => new { x.Result.RunId, x.Result.Input.EventId, x.Result.Input.PlacementId, x.Result.CreatedAt, x.Result.Total }) }; });
app.MapGet("/api/runs/{id}", (string id, EngineService engine) => engine.State().Runs.FirstOrDefault(x => x.Result.RunId == id)?.Result is { } run ? Results.Ok(run) : Results.NotFound());
app.MapGet("/api/where-used/{componentId}", (string componentId, EngineService engine) => { var s = engine.State(); return new { models = s.Models.Where(x => x.Components.Any(c => c.ComponentId == componentId)), runs = s.Runs.Where(x => x.Result.Months.Any(m => m.Segments.Any(p => p.Traces.Any(t => t.ComponentId == componentId)))).Select(x => x.Result.RunId) }; });
app.MapGet("/api/impact/reference/{referenceId}", (string referenceId, EngineService engine) =>
{
    var s = engine.State();
    return new
    {
        mode = "Conservative evidence scan; includes every reference supplied to a component",
        runs = s.Runs.Where(x => x.Result.Months.SelectMany(m => m.Segments).SelectMany(p => p.Traces)
        .Any(t => t.Input["refs"] is System.Text.Json.Nodes.JsonArray a && a.Any(r => r?["id"]?.GetValue<string>() == referenceId))).Select(x => new { x.Result.RunId, x.Result.Input.PlacementId, x.Result.Input.EventId })
    };
});
app.MapPost("/api/publications/preview", (PublicationPreviewRequest request, EngineService engine) => engine.Preview(request));
app.MapPost("/api/publications/publish", (PublishRequest request, EngineService engine) => engine.Publish(request.Token));
app.MapPost("/api/calculations/simulate", (CalculationRequest request, EngineService engine) => engine.Simulate(request));
app.MapPost("/api/calculations/execute", (CalculationRequest request, EngineService engine) => engine.Execute(request));
app.MapPost("/api/months/settle", (SettleRequest request, EngineService engine) => { engine.Settle(request.PlacementId, request.Month, request.ExpectedRunId, request.Actor); return Results.Ok(new { status = "PaidDemo" }); });
app.MapGet("/api/examples/publication", () => DemoData.Initial());
app.MapGet("/api/examples/event", () => DemoData.Event());
app.MapGet("/api/examples/emergency", (EngineService engine) => DemoData.Emergency(engine.State()));
app.MapPost("/api/demo/initialize", async (EngineService engine) =>
{
    if (engine.State().Publications.Count > 0) return Results.Ok(new { status = "already initialized" });
    var preview = await engine.Preview(DemoData.Initial()); engine.Publish(preview.Token);
    return Results.Ok(new { status = "initialized", preview.Results });
});
app.Run();
public sealed record SettleRequest(string PlacementId, DateOnly Month, string ExpectedRunId, string Actor);
public partial class Program;
