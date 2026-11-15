using System.Text.Json.Nodes;
using Rules.Api.Demo;
using Rules.Domain;
using Xunit;

namespace Rules.Tests;

public sealed class BusinessEdgeTests
{
    [Fact]
    public async Task New_emergency_logic_and_output_roles_run_in_Zen_without_new_CSharp_handler()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var request = DemoData.Emergency(f.Service.State());
        var preview = await f.Service.Preview(request); f.Service.Publish(preview.Token);
        var r = await f.Service.Execute(request.Cases[0]);
        var emergency = r.Months.Single().Segments.Single().Lines.Single(x => x.ComponentId == "emergency");
        Assert.Equal(200m, emergency.Amount);
        var trace = r.Months[0].Segments[0].Traces.Single(x => x.Id == emergency.TraceId);
        Assert.Contains(trace.Steps, x => x.Key == "quantity" && x.Value!.GetValue<decimal>() == 2m);
        Assert.Contains(trace.Steps, x => x.Key == "amount" && x.Expression.Contains("item.override"));
    }
    [Fact]
    public async Task Departure_cancels_pending_months_and_preserves_zero_reason()
    {
        using var f = new EngineFixture(); await f.Initialize(); await f.Service.Execute(DemoData.Event());
        var request = DemoData.Event("departure", 2);
        request.Periods[0].Facts["active"] = false;
        var result = await f.Service.Execute(request);
        Assert.Equal(0m, result.Total); Assert.Equal(0m, f.Service.State().Ledger.Sum(x => x.Amount));
        Assert.All(result.Months.SelectMany(x => x.Segments).SelectMany(x => x.Traces), x => Assert.Equal("Skipped", x.Status));
    }
    [Fact]
    public async Task Missing_reference_coverage_is_rejected_even_outside_simulation_month()
    {
        using var f = new EngineFixture(); var request = DemoData.Initial();
        var references = request.Package.References.Select(x => x.Id == "food-price" ? x with { To = new(2026, 12, 31) } : x).ToArray();
        var bad = request with { Package = request.Package with { References = references } };
        var error = await Assert.ThrowsAsync<BusinessException>(() => f.Service.Preview(bad));
        Assert.Equal("VALIDITY_GAP", error.Code); Assert.Empty(f.Service.State().Publications);
    }
    [Fact]
    public async Task Leap_year_partial_month_uses_actual_days_and_item_override()
    {
        using var f = new EngineFixture(); var request = DemoData.Initial();
        // Reuse the component/template contract with February 2028 dates.
        var from = new DateOnly(2028, 2, 1); var to = new DateOnly(2028, 2, 29);
        var p = request.Package with
        {
            Components = request.Package.Components.Select(x => x with { From = from, To = to }).ToArray(),
            Models = request.Package.Models.Select(x => x with { From = from, To = to }).ToArray(),
            References = request.Package.References.Select(x => x with { From = from, To = to }).ToArray(),
            Agreements = request.Package.Agreements.Select(x => x with { From = from, To = to }).ToArray()
        };
        var ev = DemoData.Event() with { From = new(2028, 2, 29), To = to, Periods = [new(from, to, DemoData.Event().Periods[0].Facts)] };
        var preview = await f.Service.Preview(new(p, [ev])); f.Service.Publish(preview.Token);
        var r = await f.Service.Execute(ev);
        var food = r.Months[0].Segments[0].Lines.Single(x => x.ItemId == "food");
        Assert.Equal(decimal.Round(1000m / 29m, 2, MidpointRounding.AwayFromZero), food.Amount);
    }
}
