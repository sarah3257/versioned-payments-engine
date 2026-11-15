using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GoRules.ZenEngine;
using Rules.Application;
using Rules.Domain;

namespace Rules.Infrastructure.Rules;

public sealed class ZenRuleRuntime : IRuleRuntime, IDisposable
{
    private readonly ZenEngine engine = new(loader: null, customNode: null);
    private readonly ConcurrentDictionary<string, Lazy<ZenExpression>> expressions = new();
    private readonly ConcurrentDictionary<string, Lazy<ZenDecision>> decisions = new();
    public string Version => "GoRules.ZenEngine/2.1.8";
    public void ValidateExpression(string expression)
    {
        try { _ = expressions.GetOrAdd(expression, x => new(() => ZenExpression.Compile(x))).Value; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { throw new BusinessException("RULE_SYNTAX", $"תחביר ZEN אינו תקין: {e.Message}"); }
    }
    public JsonNode? Evaluate(string expression, JsonObject context)
    {
        try
        {
            if (expressions.Count > 2000) throw new BusinessException("RULE_CAPACITY", "מגבלת הביטויים בזיכרון הושגה; נדרש אתחול מבוקר");
            var compiled = expressions.GetOrAdd(expression, x => new(() => ZenExpression.Compile(x))).Value;
            return Parse(compiled.Evaluate(new JsonBuffer(context.ToJsonString())));
        }
        catch (BusinessException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { throw new BusinessException("RULE_EVALUATION", $"כשל בהרצת ביטוי ZEN: {e.Message}"); }
    }
    public async Task<(JsonArray Rows, JsonNode? Trace)> EvaluateTable(DecisionTable table, JsonObject facts)
    {
        var definition = BuildTable(table);
        var hash = Json.Hash(table);
        try
        {
            if (decisions.Count > 1000) throw new BusinessException("RULE_CAPACITY", "מגבלת הטבלאות בזיכרון הושגה");
            var decision = decisions.GetOrAdd(hash, _ => new(() => engine.CreateDecision(new JsonBuffer(definition.ToJsonString())))).Value;
            var typedFacts = (JsonObject)facts.DeepClone();
            foreach (var column in table.Columns.Where(x => x.Type == "date"))
            {
                var parts = column.Field.Split('.'); var target = typedFacts;
                foreach (var part in parts[..^1]) target = target[part]!.AsObject();
                if (target[parts[^1]] is { } value) target[parts[^1]] = DateOnly.ParseExact(value.GetValue<string>(), "yyyy-MM-dd").DayNumber;
            }
            var result = await decision.Evaluate(new JsonBuffer(typedFacts.ToJsonString()), new ZenEvaluateOptions(MaxDepth: 8, Trace: true));
            var node = Parse(result.Result);
            // Collect returns an array; first-hit returns one object.
            var rows = node is JsonArray a ? a : node is JsonObject o ? new JsonArray(o.DeepClone()) : new JsonArray();
            JsonNode? trace = result.Trace is null ? null : Json.Node(result.Trace.Select(x => new { id = x.Key, x.Value.Name, x.Value.Order, output = x.Value.Output.ToString() }));
            return (rows, trace);
        }
        catch (BusinessException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { throw new BusinessException("TABLE_EVALUATION", $"כשל בהרצת טבלת ZEN: {e.Message}"); }
    }
    private static JsonNode? Parse(JsonBuffer buffer) => JsonNode.Parse(buffer.ToString());
    private static JsonObject BuildTable(DecisionTable table)
    {
        var keys = table.Rows.SelectMany(x => x.Outputs.Select(p => p.Key)).Distinct().Order().ToArray();
        var rules = new JsonArray();
        foreach (var row in table.Rows)
        {
            var r = new JsonObject { ["_id"] = row.Id, ["out_row"] = System.Text.Json.JsonSerializer.Serialize(row.Id) };
            foreach (var column in table.Columns) r[column.Id] = Unary(Typed(row.Conditions[column.Id], column.Type));
            foreach (var key in keys) r["out_" + key] = row.Outputs[key]?.ToJsonString() ?? "null";
            rules.Add(r);
        }
        return new JsonObject
        {
            ["contentType"] = "application/vnd.gorules.decision",
            ["nodes"] = new JsonArray(
                Node("input", "inputNode", new JsonObject()),
                Node("table", "decisionTableNode", new JsonObject
                {
                    ["hitPolicy"] = table.HitPolicy == "first" ? "first" : "collect",
                    ["inputs"] = Json.Node(table.Columns.Select(x => new { id = x.Id, name = x.Field, field = x.Field })),
                    ["outputs"] = new JsonArray(new[] { new JsonObject { ["id"] = "out_row", ["name"] = "rowId", ["field"] = "rowId" } }
                        .Concat(keys.Select(x => new JsonObject { ["id"] = "out_" + x, ["name"] = x, ["field"] = x })).Cast<JsonNode?>().ToArray()),
                    ["rules"] = rules,
                    ["passThrough"] = false,
                    ["executionMode"] = "single"
                }), Node("output", "outputNode", new JsonObject())),
            ["edges"] = Json.Node(new[] { new { id = "a", sourceId = "input", targetId = "table", type = "edge" }, new { id = "b", sourceId = "table", targetId = "output", type = "edge" } })
        };
    }
    private static JsonObject Node(string id, string type, JsonObject content) => new() { ["id"] = id, ["name"] = id, ["type"] = type, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["content"] = content };
    private static Condition Typed(Condition condition, string type)
    {
        if (type != "date" || condition.Operator is "any" or "missing") return condition;
        JsonNode? Convert(JsonNode? value) => value is null ? null : JsonValue.Create(DateOnly.ParseExact(value.GetValue<string>(), "yyyy-MM-dd").DayNumber);
        return condition with { Value = condition.Value is JsonArray a ? new JsonArray(a.Select(Convert).ToArray()) : Convert(condition.Value), To = Convert(condition.To) };
    }
    private static string Unary(Condition c) => c.Operator switch
    {
        "any" => "",
        "missing" => "null",
        "eq" => c.Value!.ToJsonString(),
        "ne" => "!= " + c.Value!.ToJsonString(),
        "gt" => "> " + c.Value!.ToJsonString(),
        "gte" => ">= " + c.Value!.ToJsonString(),
        "lt" => "< " + c.Value!.ToJsonString(),
        "lte" => "<= " + c.Value!.ToJsonString(),
        "between" => $"[{c.Value!.ToJsonString()}..{c.To!.ToJsonString()}]",
        "in" => string.Join(", ", ((JsonArray)c.Value!).Select(x => x!.ToJsonString())),
        _ => throw new BusinessException("CONDITION", "אופרטור לא נתמך")
    };
    public void Dispose()
    {
        foreach (var x in expressions.Values) if (x.IsValueCreated) x.Value.Dispose();
        foreach (var x in decisions.Values) if (x.IsValueCreated) x.Value.Dispose();
        engine.Dispose();
    }
}
