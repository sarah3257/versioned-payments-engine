using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;

// Stable numeric explanations are necessary for replay across machines/cultures.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
var builder = WebApplication.CreateBuilder(args);
if (!builder.Configuration.GetSection("urls").Exists()) builder.WebHost.UseUrls("http://127.0.0.1:5088");
var dbPath = builder.Configuration["DatabasePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "payments-demo.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
builder.Services.AddDbContext<EngineDb>(o => o.UseSqlite($"Data Source={dbPath};Foreign Keys=True;Default Timeout=30"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<WriterGate>();
builder.Services.AddScoped<PublicationService>(); builder.Services.AddScoped<PlanBuilder>(); builder.Services.AddScoped<RunService>();
builder.Services.AddSingleton<IComponentCalculator>(new ItemCalculator(ComponentKind.Maintenance));
builder.Services.AddSingleton<IComponentCalculator>(new ItemCalculator(ComponentKind.Staff));
builder.Services.AddSingleton<IComponentCalculator, BasketCalculator>();
builder.Services.AddSingleton<IComponentCalculator, ProfitCalculator>();
builder.Services.AddSingleton<IComponentCalculator, TaxCalculator>();
builder.Services.AddSingleton<CalculationEngine>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (BusinessException e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (DbUpdateException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "התנגשות נתונים: מזהה/גרסה קיימים או פעולה אסורה על היסטוריה" }); }
    catch (BadHttpRequestException e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) { app.Logger.LogError(e, "Request failed"); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "שגיאת שרת. הבקשה לא הושלמה; יש לבדוק את יומן השרת" }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/health", () => new { status = "ok", engine = "engine-v1", database = "SQLite — local POC", scope = "single-month explicit factor" });
app.MapGet("/api/catalog", async (EngineDb db) => new
{
    models = await db.ModelVersions.AsNoTracking().Include(x => x.Components).ToListAsync(),
    components = await db.ComponentVersions.AsNoTracking().Include(x => x.Items).Include(x => x.Rows).ThenInclude(x => x.Conditions).AsSplitQuery().ToListAsync(),
    prices = await db.PriceVersions.AsNoTracking().ToListAsync(), consents = await db.Consents.AsNoTracking().ToListAsync(),
    taxRates = await db.TaxRateVersions.AsNoTracking().ToListAsync(), agreements = await db.AgreementVersions.AsNoTracking().ToListAsync()
});
app.MapGet("/api/runs", async (EngineDb db) => await db.CalculationRuns.AsNoTracking().OrderByDescending(r => r.CompletedAtUtc)
    .Take(100).Select(r => new { r.Id, r.RequestKey, r.ModelId, r.ModelVersion, r.EventDate, r.Institution, r.Status, r.CompletedAtUtc }).ToListAsync());
app.MapGet("/api/runs/{id:guid}", async (Guid id, EngineDb db) =>
{
    var run = await db.CalculationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) ?? throw new BusinessException("ריצה לא נמצאה", 404);
    return Results.Content(run.ResultJson, "application/json");
});
app.MapGet("/api/runs/{id:guid}/snapshot", async (Guid id, EngineDb db) =>
{
    var run = await db.CalculationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) ?? throw new BusinessException("ריצה לא נמצאה", 404);
    return Results.Content(run.PlanJson, "application/json");
});
app.MapPost("/api/runs/{id:guid}/replay", (Guid id, RunService service) => service.ReplayAsync(id));
app.MapPost("/api/calculations", (CalculateRequest request, RunService service) => service.CalculateAsync(request));
app.MapPost("/api/simulations", (CalculateRequest request, RunService service) => service.CalculateAsync(request, simulation: true));
app.MapPost("/api/components/publish", async (PublishComponent p, PublicationService service, WriterGate gate) => await Write(() => service.PublishComponentAsync(p), gate));
app.MapPost("/api/models/publish", async (PublishModel p, PublicationService service, WriterGate gate) => await Write(() => service.PublishModelAsync(p), gate));
app.MapPost("/api/consents", async (PublishConsent p, PublicationService service, WriterGate gate) => await Write(() => service.PublishConsentAsync(p), gate));
app.MapPost("/api/prices/publish", async (PublishPrice p, PublicationService service, WriterGate gate) => await Write(() => service.PublishPriceAsync(p), gate));
app.MapPost("/api/agreements/publish", async (PublishAgreement p, PublicationService service, WriterGate gate) => await Write(() => service.PublishAgreementAsync(p), gate));
app.MapPost("/api/tax-rates/publish", async (PublishTax p, PublicationService service, WriterGate gate) => await Write(() => service.PublishTaxAsync(p), gate));
app.MapGet("/api/schema", (EngineDb db) => db.Model.GetEntityTypes().Select(e => new
{
    table = e.GetTableName(), columns = e.GetProperties().Select(p => new { name = p.Name, type = p.ClrType.Name, nullable = p.IsNullable }),
    relations = e.GetForeignKeys().Select(f => new { columns = f.Properties.Select(p => p.Name), target = f.PrincipalEntityType.GetTableName() })
}));
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EngineDb>();
    await EngineDb.InitializeAsync(db);
    if (builder.Configuration.GetValue("SeedDemo", true)) await DemoSeed.InitializeAsync(db);
}
await app.RunAsync();

static async Task<T> Write<T>(Func<Task<T>> action, WriterGate gate)
{
    await gate.Lock.WaitAsync(); try { return await action(); } finally { gate.Lock.Release(); }
}

public partial class Program { }
