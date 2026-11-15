using System.Text.Json.Nodes;
using Rules.Domain;

namespace Rules.Application.Rules;

public sealed class ConditionEvaluator(IRuleRuntime runtime)
{
    private static readonly HashSet<string> Operators = ["eq", "ne", "gt", "gte", "lt", "lte", "between", "in", "any", "missing"];
    public static JsonNode? Get(JsonObject facts, string path)
    {
        JsonNode? value = facts;
        foreach (var part in path.Split('.')) value = (value as JsonObject)?[part];
        return value;
    }
    public static void Validate(Condition c)
    {
        if (!Operators.Contains(c.Operator) || string.IsNullOrWhiteSpace(c.Field) || c.Field.Split('.').Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Za-z][A-Za-z0-9_]*$")))
            throw new BusinessException("CONDITION", "שדה או אופרטור לא נתמכים");
        if (c.Operator is not ("any" or "missing") && c.Value is null)
            throw new BusinessException("CONDITION_VALUE", "ערך תנאי חסר; יש להשתמש ב־missing או any במפורש");
        if (c.Operator == "between" && c.To is null || c.Operator == "in" && c.Value is not JsonArray)
            throw new BusinessException("CONDITION_VALUE", "טווח או רשימת ערכים אינם תקינים");
    }
    public ConditionEvidence Evaluate(Condition c, JsonObject facts, bool allowMissing = false)
    {
        Validate(c);
        var actual = Get(facts, c.Field);
        if (actual is null && allowMissing && c.Operator is not ("any" or "missing"))
            return new(c.Field, c.Operator, null, c.Value?.DeepClone(), c.To?.DeepClone(), false);
        if (actual is null && c.Operator is not ("any" or "missing"))
            throw new BusinessException("MISSING_FACT", $"נתון חסר: {c.Field}");
        var context = new JsonObject { ["actual"] = actual?.DeepClone(), ["expected"] = c.Value?.DeepClone(), ["upper"] = c.To?.DeepClone() };
        if (c.Operator is "gt" or "gte" or "lt" or "lte" or "between" &&
            actual is JsonValue a && a.TryGetValue<string>(out var dateText) && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", out var date) &&
            c.Value is JsonValue b && b.TryGetValue<string>(out var expectedText) && DateOnly.TryParseExact(expectedText, "yyyy-MM-dd", out var expected))
        {
            context["actual"] = date.DayNumber; context["expected"] = expected.DayNumber;
            if (c.To is not null) context["upper"] = DateOnly.ParseExact(c.To.GetValue<string>(), "yyyy-MM-dd").DayNumber;
        }
        string expression = c.Operator switch
        {
            "any" => "true",
            "missing" => "actual == null",
            "eq" => "actual == expected",
            "ne" => "actual != expected",
            "gt" => "actual > expected",
            "gte" => "actual >= expected",
            "lt" => "actual < expected",
            "lte" => "actual <= expected",
            "between" => "actual >= expected and actual <= upper",
            "in" => "actual in expected",
            _ => throw new InvalidOperationException()
        };
        var result = runtime.Evaluate(expression, context)?.GetValue<bool>()
            ?? throw new BusinessException("CONDITION_RESULT", "תנאי לא החזיר ערך בוליאני");
        return new(c.Field, c.Operator, actual?.DeepClone(), c.Value?.DeepClone(), c.To?.DeepClone(), result);
    }
}
