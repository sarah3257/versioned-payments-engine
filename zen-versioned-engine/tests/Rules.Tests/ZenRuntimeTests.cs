using System.Text.Json.Nodes;
using Rules.Domain;
using Rules.Infrastructure.Rules;
using Xunit;

namespace Rules.Tests;

public sealed class ZenRuntimeTests
{
    [Fact]
    public void Real_native_engine_filters_sums_and_preserves_decimal_values()
    {
        using var engine = new ZenRuleRuntime();
        var input = JsonNode.Parse("""{"rows":[{"amount":0.1,"eligible":true},{"amount":0.2,"eligible":true},{"amount":7,"eligible":false}]}""")!.AsObject();
        Assert.Equal(0.3m, engine.Evaluate("sum(map(filter(rows, #.eligible), #.amount))", input)!.GetValue<decimal>());
        Assert.Equal(123456789.123456m, engine.Evaluate("123456789.123456", new())!.GetValue<decimal>());
    }
    [Fact]
    public async Task Real_native_decision_table_collects_both_matching_rows()
    {
        using var engine = new ZenRuleRuntime();
        var table = new DecisionTable([new("age", "age", "number")], "collect", "zero", [
            new("one", new() { ["age"] = new("age", "gte", JsonValue.Create(18)) }, new() { ["amount"] = 10 }, "rule-1"),
            new("two", new() { ["age"] = new("age", "between", JsonValue.Create(18), JsonValue.Create(21)) }, new() { ["amount"] = 20 }, "rule-2")]);
        var result = await engine.EvaluateTable(table, new() { ["age"] = 21 });
        Assert.Equal(2, result.Rows.Count); Assert.NotNull(result.Trace);
    }
    [Fact]
    public async Task Date_and_list_conditions_match_with_explicit_types()
    {
        using var engine = new ZenRuleRuntime();
        var table = new DecisionTable([new("date", "date", "date"), new("type", "type", "string")], "unique", "zero", [
            new("dated", new() { ["date"] = new("date", "between", JsonValue.Create("2026-01-01"), JsonValue.Create("2026-12-31")),
                ["type"] = new("type", "in", new JsonArray("A", "B")) }, new() { ["amount"] = 10 }, "source")]);
        var result = await engine.EvaluateTable(table, new() { ["date"] = "2026-11-15", ["type"] = "B" });
        Assert.Single(result.Rows);
    }
}
