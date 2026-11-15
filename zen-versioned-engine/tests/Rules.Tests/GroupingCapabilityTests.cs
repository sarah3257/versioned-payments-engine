using System.Text.Json.Nodes;
using Rules.Domain;
using Rules.Infrastructure.Rules;
using Xunit;
using Xunit.Abstractions;

namespace Rules.Tests;

public sealed class GroupingCapabilityTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_native_grouping_and_full_profit_chain()
    {
        using var runtime = new ZenRuleRuntime();
        var context = JsonNode.Parse("""{"rows":[{"key":"a","group":"A","amount":1000,"profitRequired":true},{"key":"b","group":"A","amount":1800,"profitRequired":true},{"key":"c","group":"B","amount":500,"profitRequired":true},{"key":"d","group":"B","amount":100,"profitRequired":false}],"groups":["A","B"],"rate":0.04}""")!.AsObject();
        foreach (var expression in new[] {
            "groupBy(rows, #.group)", "group_by(rows, #.group)", "group(rows, #.group)",
            "distinct(map(rows, #.group))", "dedup(map(rows, #.group))", "unique(map(rows, #.group))"
        })
        {
            try { output.WriteLine(expression + " => " + runtime.Evaluate(expression, context)?.ToJsonString()); }
            catch (BusinessException e) { output.WriteLine(expression + " => UNSUPPORTED: " + e.Message); }
        }
        const string chain = "map(groups as g, {group: g, basis: sum(map(filter(rows, #.profitRequired and #.group == g), #.amount)), amount: sum(map(filter(rows, #.profitRequired and #.group == g), #.amount)) * rate, basisKeys: map(filter(rows, #.profitRequired and #.group == g), #.key)})";
        var result = runtime.Evaluate(chain, context)!.AsArray();
        output.WriteLine("Known-group full chain => " + result.ToJsonString());
        Assert.Equal(112m, result[0]!["amount"]!.GetValue<decimal>());
        Assert.Equal(20m, result[1]!["amount"]!.GetValue<decimal>());
    }
}
