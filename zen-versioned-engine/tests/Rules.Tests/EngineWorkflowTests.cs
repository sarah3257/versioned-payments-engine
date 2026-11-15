using System.Text.Json.Nodes;
using Rules.Api.Demo;
using Rules.Application;
using Rules.Application.Execution;
using Rules.Application.Rules;
using Rules.Domain;
using Rules.Infrastructure.Persistence;
using Rules.Infrastructure.Rules;
using Xunit;

namespace Rules.Tests;

public sealed class EngineFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zen-poc-tests", Guid.NewGuid().ToString("N"), "engine.db");
    public ZenRuleRuntime Runtime { get; } = new();
    public SqliteEngineStore Store { get; }
    public EngineService Service { get; }
    public EngineFixture()
    {
        Store = new(Path);
        Service = new(Store, new(new(Runtime, new(Runtime)), Runtime), new(new(Runtime)), Runtime);
    }
    public async Task Initialize() { var preview = await Service.Preview(DemoData.Initial()); Service.Publish(preview.Token); }
    public async Task Price(int version, decimal amount, DateOnly? from = null)
    {
        var p = new PublicationPackage("price-" + version, "tester", [], [], [],
            [new("food-price", version, "Maintenance", from ?? new(2026, 1, 1), new(2027, 12, 31), new() { ["amount"] = amount }, "תיקון מחירון", version - 1)], []);
        var preview = await Service.Preview(new(p, [DemoData.Event()])); Service.Publish(preview.Token);
    }
    public void Dispose() => Runtime.Dispose();
}

public sealed class EngineWorkflowTests
{
    [Fact]
    public async Task Golden_case_full_layers_months_and_trace()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var result = await f.Service.Execute(DemoData.Event());
        Assert.Equal(7226.32m, result.Total);
        Assert.All(result.Months, x => Assert.Equal(3613.16m, x.Total));
        var traces = result.Months.SelectMany(x => x.Segments).SelectMany(x => x.Traces).ToArray();
        Assert.Contains(traces, x => x.Table.Count(r => r.Matched) == 2);
        Assert.Contains(traces, x => x.Steps.Any(v => v.Key == "basis" && v.Value!.GetValue<decimal>() == 2800m));
        Assert.All(result.Months.SelectMany(x => x.Segments).SelectMany(x => x.Lines), line => Assert.Contains(traces, t => t.Id == line.TraceId));
    }
    [Fact]
    public async Task Simulation_does_not_change_ledger_and_publication_never_recalculates_payments()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var before = f.Service.State(); await f.Service.Simulate(DemoData.Event());
        Assert.Equal(Json.Hash(before), Json.Hash(f.Service.State()));
        await f.Service.Execute(DemoData.Event()); var ledgerHash = Json.Hash(f.Service.State().Ledger);
        await f.Price(2, 1200m);
        Assert.Equal(ledgerHash, Json.Hash(f.Service.State().Ledger));
    }
    [Fact]
    public async Task Paid_retro_twice_posts_only_incremental_difference_and_preserves_original()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var original = await f.Service.Execute(DemoData.Event());
        foreach (var month in original.Months) f.Service.Settle("placement-1", month.Month, original.RunId, "tester");
        await f.Price(2, 1200m);
        var second = await f.Service.Execute(DemoData.Event("retro-1", 2));
        Assert.Equal(490.88m, f.Service.State().Ledger.Where(x => x.RunId == second.RunId).Sum(x => x.Amount));
        await f.Price(3, 1300m);
        var third = await f.Service.Execute(DemoData.Event("retro-2", 3));
        Assert.Equal(245.44m, f.Service.State().Ledger.Where(x => x.RunId == third.RunId).Sum(x => x.Amount));
        Assert.Equal(third.Total, f.Service.State().Ledger.Sum(x => x.Amount));
        Assert.Equal(Json.Write(original), Json.Write(f.Service.State().Runs.First().Result));
    }
    [Fact]
    public async Task Pending_retro_cancels_without_deleting_and_retry_is_idempotent()
    {
        using var f = new EngineFixture(); await f.Initialize(); await f.Service.Execute(DemoData.Event());
        await f.Price(2, 1200m);
        var r = await f.Service.Execute(DemoData.Event("new-event", 2));
        var count = f.Service.State().Ledger.Count;
        Assert.Equal(r.RunId, (await f.Service.Execute(DemoData.Event("new-event", 2))).RunId);
        Assert.Equal(count, f.Service.State().Ledger.Count);
        Assert.Contains(f.Service.State().Ledger, x => x.Kind == "Canceled");
        Assert.Equal(r.Total, f.Service.State().Ledger.Sum(x => x.Amount));
    }
    [Fact]
    public async Task Mid_month_reference_change_splits_and_prorates()
    {
        using var f = new EngineFixture(); await f.Initialize(); await f.Price(2, 2000m, new(2026, 9, 16));
        var r = await f.Service.Execute(DemoData.Event());
        Assert.Equal(2, r.Months[0].Segments.Length); Assert.Equal(4226.76m, r.Months[0].Total);
        Assert.Equal(4840.36m, r.Months[1].Total);
    }
    [Fact]
    public async Task Failure_in_second_month_leaves_no_partial_financial_state()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var req = DemoData.Event() with { Periods = [new(new(2026, 9, 1), new(2026, 9, 30), DemoData.Event().Periods[0].Facts)] };
        await Assert.ThrowsAsync<BusinessException>(() => f.Service.Execute(req));
        Assert.Empty(f.Service.State().Runs); Assert.Empty(f.Service.State().Ledger);
        Assert.Single(f.Service.State().Failures);
    }
    [Fact]
    public async Task Stale_preview_and_same_event_with_different_body_are_rejected()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var p = new PublicationPackage("new-component", "tester", [], [f.Service.State().Components[0] with { Id = "maintenance-extra" }], [], [], []);
        var preview = await f.Service.Preview(new(p, [DemoData.Event()]));
        await f.Service.Execute(DemoData.Event());
        Assert.Equal("STALE_PREVIEW", Assert.Throws<BusinessException>(() => f.Service.Publish(preview.Token)).Code);
        var error = await Assert.ThrowsAsync<BusinessException>(() => f.Service.Execute(DemoData.Event() with { SourceRevision = 2 }));
        Assert.Equal("REQUEST_CONFLICT", error.Code);
    }
    [Fact]
    public async Task New_component_and_model_publish_without_code_changes()
    {
        using var f = new EngineFixture(); await f.Initialize(); var state = f.Service.State();
        var extra = state.Components.Single(x => x.Id == "maintenance") with { Id = "extra", Name = "רכיב נוסף מאותה תבנית" };
        var model = state.Models[0] with { Version = 2, Supersedes = 1, Components = [.. state.Models[0].Components, new("extra", "extra", 1, [])] };
        var p = new PublicationPackage("extension", "tester", [], [extra], [model], [], []);
        var preview = await f.Service.Preview(new(p, [DemoData.Event()])); f.Service.Publish(preview.Token);
        Assert.Equal(9680.72m, (await f.Service.Execute(DemoData.Event())).Total);
        Assert.Equal(4, f.Service.State().Templates.Count);
    }
    [Fact]
    public async Task Old_event_revision_is_rejected_and_database_survives_reopening()
    {
        using var f = new EngineFixture(); await f.Initialize(); var r = await f.Service.Execute(DemoData.Event());
        var e = await Assert.ThrowsAsync<BusinessException>(() => f.Service.Execute(DemoData.Event("late-old", 1)));
        Assert.Equal("STALE_EVENT", e.Code);
        var reopened = new SqliteEngineStore(f.Path).Read().State;
        Assert.Equal(r.RunId, reopened.Runs.Single().Result.RunId);
    }
}
