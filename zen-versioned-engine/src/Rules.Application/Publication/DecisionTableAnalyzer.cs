using System.Globalization;
using System.Text.Json.Nodes;
using Rules.Application.Rules;
using Rules.Domain;

namespace Rules.Application.Publication;

// Exact partition analysis for the restricted table language, not arbitrary expressions.
// Numeric/date boundaries, boolean values and equality categories define equivalent cells.
public sealed class DecisionTableAnalyzer(ConditionEvaluator evaluator)
{
    public void Validate(DecisionTable table, ISet<string>? nullableFields = null)
    {
        var domains = table.Columns.Select(c => Representatives(c, table.Rows.Select(r => r.Conditions[c.Id]).ToArray(), nullableFields?.Contains(c.Field) == true)).ToArray();
        long combinations = 1;
        foreach (var domain in domains) { combinations *= domain.Length; if (combinations > 100000) throw new BusinessException("TABLE_ANALYSIS_LIMIT", "הטבלה מורכבת מדי לניתוח כיסוי מלא ב־POC; יש לפצל אותה"); }
        if (combinations * table.Rows.Length * table.Columns.Length > 2_000_000)
            throw new BusinessException("TABLE_ANALYSIS_LIMIT", "תקציב בדיקת הטבלה חורג ממגבלת העבודה; יש לפצל את הטבלה");
        var facts = new JsonObject();
        void Walk(int index)
        {
            if (index == table.Columns.Length)
            {
                var matches = table.Rows.Count(row => table.Columns.All(c => evaluator.Evaluate(row.Conditions[c.Id], facts, allowMissing: true).Matched));
                if (table.HitPolicy == "unique" && matches > 1) throw new BusinessException("TABLE_OVERLAP", "חפיפה בטבלת התאמה יחידה. דוגמה: " + facts.ToJsonString());
                if (table.HitPolicy == "collect" && matches > 1 && !table.AllowOverlap) throw new BusinessException("TABLE_OVERLAP_CONFIRMATION", "חפיפת COLLECT מחייבת allowOverlap מפורש. דוגמה: " + facts.ToJsonString());
                if (table.NoMatch == "error" && matches == 0) throw new BusinessException("TABLE_GAP", "חור בכיסוי טבלת החלטה. דוגמה: " + facts.ToJsonString());
                return;
            }
            foreach (var value in domains[index])
            {
                Set(facts, table.Columns[index].Field, value); Walk(index + 1);
            }
        }
        Walk(0);
    }
    private static JsonNode?[] Representatives(TableColumn column, Condition[] conditions, bool nullable)
    {
        var values = conditions.SelectMany(c => c.Operator == "in" ? ((JsonArray)c.Value!).ToArray() : new[] { c.Value, c.To }).Where(x => x is not null).ToArray();
        // Missing is an explicit category only where the table declares it; otherwise missing input is a validation error.
        var missing = nullable || conditions.Any(x => x.Operator == "missing");
        var result = new List<JsonNode?>();
        if (column.Type == "number")
        {
            var boundaries = values.Select(v => decimal.Parse(v!.ToJsonString(), CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
            if (boundaries.Any(x => x < -1_000_000_000_000_000m || x > 1_000_000_000_000_000m)) throw new BusinessException("TABLE_NUMBER_RANGE", "גבולות מספריים חורגים מטווח הניתוח");
            if (boundaries.Length == 0) result.Add(JsonValue.Create(0m));
            else
            {
                result.Add(JsonValue.Create(boundaries[0] - 1));
                for (var i = 0; i < boundaries.Length; i++)
                {
                    result.Add(JsonValue.Create(boundaries[i]));
                    if (i + 1 < boundaries.Length) result.Add(JsonValue.Create(boundaries[i] / 2 + boundaries[i + 1] / 2));
                }
                result.Add(JsonValue.Create(boundaries[^1] + 1));
            }
        }
        else if (column.Type == "date")
        {
            var boundaries = values.Select(v => DateOnly.ParseExact(v!.GetValue<string>(), "yyyy-MM-dd")).Distinct().Order().ToArray();
            if (boundaries.Length == 0) result.Add(JsonValue.Create("2000-01-01"));
            foreach (var d in boundaries)
                foreach (var day in new[] { Math.Max(0, d.DayNumber - 1), d.DayNumber, Math.Min(DateOnly.MaxValue.DayNumber, d.DayNumber + 1) }) result.Add(JsonValue.Create(DateOnly.FromDayNumber(day).ToString("yyyy-MM-dd")));
        }
        else if (column.Type == "boolean") { result.Add(JsonValue.Create(true)); result.Add(JsonValue.Create(false)); }
        else if (column.Type == "string")
        {
            if (conditions.Any(x => x.Operator is not ("eq" or "ne" or "in" or "any" or "missing")))
                throw new BusinessException("TABLE_STRING_OPERATOR", "מחרוזות תומכות בשוויון ורשימות; השוואת טווח נדרשת כשדה מספר או תאריך");
            result.AddRange(values.Select(x => x!.DeepClone()));
            var other = "__OTHER__"; while (values.Any(x => x!.GetValue<string>() == other)) other += "_";
            result.Add(JsonValue.Create(other));
        }
        else throw new BusinessException("TABLE_TYPE", "טיפוס עמודת טבלה אינו נתמך");
        if (missing) result.Add(null);
        return result.DistinctBy(x => x?.ToJsonString() ?? "null").ToArray();
    }
    private static void Set(JsonObject facts, string path, JsonNode? value)
    {
        var parts = path.Split('.'); var target = facts;
        foreach (var part in parts[..^1]) { target[part] ??= new JsonObject(); target = target[part]!.AsObject(); }
        target[parts[^1]] = value?.DeepClone();
    }
}
