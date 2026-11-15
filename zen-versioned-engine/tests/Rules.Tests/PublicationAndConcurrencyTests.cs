using System.Text.Json.Nodes;
using Rules.Api.Demo;
using Rules.Application.Publication;
using Rules.Application.Rules;
using Rules.Domain;
using Xunit;

namespace Rules.Tests;

public sealed class PublicationAndConcurrencyTests
{
    [Fact]
    public async Task Same_event_concurrently_commits_one_run_and_one_ledger()
    {
        using var f = new EngineFixture(); await f.Initialize();
        var outputs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => f.Service.Execute(DemoData.Event()))));
        Assert.Single(outputs.Select(x => x.RunId).Distinct()); Assert.Single(f.Service.State().Runs);
        Assert.Equal(outputs[0].Total, f.Service.State().Ledger.Sum(x => x.Amount));
    }
    [Fact]
    public async Task Publish_retry_is_idempotent_and_versions_are_immutable()
    {
        using var f = new EngineFixture(); var request = DemoData.Initial(); var preview = await f.Service.Preview(request);
        var one = f.Service.Publish(preview.Token); var two = f.Service.Publish(preview.Token);
        Assert.Equal(one, two); Assert.Single(f.Service.State().Publications);
        var bad = request.Package with { RequestId = "different-publication" };
        Assert.Equal("IMMUTABLE_VERSION", (await Assert.ThrowsAsync<BusinessException>(() => f.Service.Preview(new(bad, request.Cases)))).Code);
    }
    [Fact]
    public async Task Unique_overlap_and_no_match_gap_are_rejected_before_publication()
    {
        using var f = new EngineFixture(); var analyzer = new DecisionTableAnalyzer(new(f.Runtime));
        DecisionRow Row(string id, string op, int value) => new(id, new() { ["age"] = new("age", op, JsonValue.Create(value)) }, new() { ["amount"] = 1 }, "source");
        var overlap = new DecisionTable([new("age", "age", "number")], "unique", "zero", [Row("a", "gte", 18), Row("b", "lte", 21)]);
        Assert.Equal("TABLE_OVERLAP", Assert.Throws<BusinessException>(() => analyzer.Validate(overlap)).Code);
        var gap = overlap with { Rows = [Row("a", "gte", 18)], NoMatch = "error" };
        Assert.Equal("TABLE_GAP", Assert.Throws<BusinessException>(() => analyzer.Validate(gap)).Code);
        var complete = overlap with { Rows = [Row("a", "gte", 18), Row("b", "lt", 18)], NoMatch = "error" };
        analyzer.Validate(complete);
    }
    [Theory]
    [InlineData("rand(10)")]
    [InlineData("d()")]
    [InlineData("fetch('https://example.com')")]
    [InlineData("a = 3")]
    [InlineData("a; b")]
    public void Unsafe_or_nondeterministic_expressions_are_rejected(string expression) => Assert.Throws<BusinessException>(() => ExpressionPolicy.Validate(expression));
    [Fact]
    public async Task Model_agreement_adoption_is_historical_and_can_change_mid_month()
    {
        using var f = new EngineFixture(); await f.Initialize(); var original = f.Service.State().Models.Single();
        var model = original with { Version = 2, Agreement = "X+1", From = new(2026, 9, 1), Components = original.Components.Where(x => x.ComponentId != "profit").ToArray() };
        var package = new PublicationPackage("agreement", "tester", [], [], [model], [], [new("institution-a", "housing", "X+1", new(2026, 9, 16), new(2027, 12, 31), "adoption", 2, 1)]);
        var preview = await f.Service.Preview(new(package, [DemoData.Event()])); f.Service.Publish(preview.Token);
        var run = await f.Service.Execute(DemoData.Event());
        Assert.Equal(new[] { 1, 2 }, run.Months[0].Segments.Select(x => x.ModelVersion));
        Assert.Equal(2, run.Months[1].Segments.Single().ModelVersion);
    }
    [Fact]
    public async Task Adding_a_column_to_new_table_version_preserves_old_trace()
    {
        using var f = new EngineFixture(); await f.Initialize(); var original = await f.Service.Execute(DemoData.Event());
        var basket = f.Service.State().Components.Single(x => x.Id == "basket");
        var column = new TableColumn("ownership", "ownership", "string");
        var template = f.Service.State().Templates.Single(x => x.Id == "decision-payment") with { Version = 2, Facts = [.. f.Service.State().Templates.Single(x => x.Id == "decision-payment").Facts, new("ownership", "string")] };
        var rows = basket.Table!.Rows.Select(x => x with { Conditions = new(x.Conditions) { ["ownership"] = new("ownership", "eq", JsonValue.Create("Private")) } }).ToArray();
        var component = basket with { Version = 2, Supersedes = 1, TemplateVersion = 2, Table = basket.Table with { Columns = [.. basket.Table.Columns, column], Rows = rows } };
        var preview = await f.Service.Preview(new(new("columns", "tester", [template], [component], [], [], []), [DemoData.Event()])); f.Service.Publish(preview.Token);
        var updated = await f.Service.Execute(DemoData.Event("table-new", 2));
        Assert.Equal(original.Total, updated.Total);
        Assert.Equal(2, original.Months[0].Segments[0].Traces.First(x => x.ComponentId == "basket").Table[0].Conditions.Length);
        Assert.Equal(3, updated.Months[0].Segments[0].Traces.First(x => x.ComponentId == "basket").Table[0].Conditions.Length);
    }
}
