using System.Globalization;
using System.Text.Json.Nodes;
using Rules.Application.Publication;
using Rules.Application.Rules;
using Rules.Domain;

namespace Rules.Application.Execution;

public sealed class ComponentExecutor(IRuleRuntime runtime, ConditionEvaluator conditions)
{
    public async Task<(PaymentLine[] Lines, ComponentTrace[] Traces)> Execute(
        ComponentVersion component, ComponentTemplate template, ComponentUse use,
        PeriodSegment period, JsonObject facts, ReferenceVersion[] references, PaymentLine[] previous)
    {
        DefinitionValidator.Fields(template.Facts, facts, component.Id);
        var applicability = use.Conditions.Select(x => conditions.Evaluate(x, facts)).ToArray();
        var traces = new List<ComponentTrace>(); var lines = new List<PaymentLine>();
        var input = new JsonObject
        {
            ["facts"] = facts.DeepClone(),
            ["p"] = component.Parameters.DeepClone(),
            ["period"] = Json.Node(new { period.From, period.To, factor = period.Factor, days = period.To.DayNumber - period.From.DayNumber + 1 }),
            ["refs"] = Json.Node(references.Select(x => new { x.Id, x.Version, x.Kind, x.Values, x.Source })),
            ["previous"] = Json.Node(previous),
            ["s"] = new JsonObject()
        };
        if (applicability.Any(x => !x.Matched) || facts["active"]?.GetValue<bool>() == false)
        {
            traces.Add(new(Guid.NewGuid().ToString("N"), component.Id, component.Version, use.Id, template.Id, template.Version,
                use.Layer, period.From, period.To, "Skipped", input, [],
                [new("assignment", false, applicability, component.Source)], null, component.Source, applicability));
            return ([], traces.ToArray());
        }
        var tableEvidence = new List<TableEvidence>(); JsonNode? nativeTrace = null;
        var tableRows = new JsonArray();
        if (component.Table is { } table)
        {
            foreach (var row in table.Rows)
            {
                var evidence = table.Columns.Select(x => conditions.Evaluate(row.Conditions[x.Id], facts,
                    allowMissing: template.Facts.Any(f => f.Name == x.Field && !f.Required))).ToArray();
                tableEvidence.Add(new(row.Id, evidence.All(x => x.Matched), evidence, row.Source));
            }
            var matches = tableEvidence.Where(x => x.Matched).ToArray();
            if (table.HitPolicy == "unique" && matches.Length > 1) throw new BusinessException("TABLE_OVERLAP", $"יותר משורה אחת התאימה ברכיב {component.Id}");
            if (table.NoMatch == "error" && matches.Length == 0) throw new BusinessException("TABLE_NO_MATCH", $"אין התאמה בטבלת {component.Id}");
            (tableRows, nativeTrace) = await runtime.EvaluateTable(table, facts);
            var expected = (table.HitPolicy == "first" ? matches.Take(1) : matches).Select(x => x.RowId).Order().ToArray();
            var actual = tableRows.Select(x => x?["rowId"]?.GetValue<string>() ?? "").Order().ToArray();
            if (!expected.SequenceEqual(actual)) throw new BusinessException("TABLE_SEMANTICS", "תוצאת טבלת ZEN אינה תואמת לתנאים המתועדים; החישוב נעצר");
        }
        input["tableRows"] = tableRows;
        JsonArray items;
        if (template.ForEach is null) items = new JsonArray(new JsonObject());
        else items = runtime.Evaluate(template.ForEach, input) as JsonArray
            ?? throw new BusinessException("COLLECTION", "ביטוי איסוף חייב להחזיר מערך");
        if (items.Count > 5000) throw new BusinessException("COLLECTION_SIZE", "מספר פריטי החישוב חורג מהמגבלה");
        if (template.GroupBy is { Length: > 0 } grouping) items = Group(items, grouping);
        foreach (var item in items)
        {
            var context = (JsonObject)input.DeepClone(); context["item"] = item?.DeepClone(); context["s"] = new JsonObject();
            var evidence = new List<StepEvidence>();
            foreach (var step in template.Steps)
            {
                var value = runtime.Evaluate(step.Expression, context);
                if (value is null || step.Assert && !value.GetValue<bool>())
                    throw new BusinessException("RULE_ASSERTION", $"רכיב {component.Id}: צעד {step.Key} נכשל או החזיר null");
                ((JsonObject)context["s"]!)[step.Key] = value.DeepClone(); evidence.Add(new(step.Key, step.Expression, value.DeepClone()));
            }
            var output = runtime.Evaluate(template.OutputExpression, context) as JsonArray
                ?? throw new BusinessException("OUTPUT_SHAPE", "פלט רכיב חייב להיות מערך שורות");
            evidence.Add(new("output", template.OutputExpression, output.DeepClone()));
            evidence.Add(new("roundingPolicy", "decimal.Round(rawAmount, 2, MidpointRounding.AwayFromZero)",
                Json.Node(new { scale = 2, mode = "AwayFromZero", level = "segment-line" })));
            var traceId = Guid.NewGuid().ToString("N");
            foreach (var row in output.OfType<JsonObject>())
            {
                var itemId = Required<string>(row, "itemId"); var group = row["group"]?.GetValue<string>() ?? "";
                var raw = Required<decimal>(row, "amount"); var amount = decimal.Round(raw, 2, MidpointRounding.AwayFromZero);
                var basisKeys = row["basisKeys"] is JsonArray basis ? basis.Select(x => x!.GetValue<string>()).ToArray() : [];
                if (basisKeys.Any(x => !previous.Any(p => p.Key == x))) throw new BusinessException("BASIS_REFERENCE", "שורת בסיס אינה משכבה קודמת");
                var key = Json.Write(new { use = use.Id, item = itemId, group, from = period.From, to = period.To });
                var explanation = template.Explanation.Replace("{amount}", amount.ToString("0.00", CultureInfo.InvariantCulture))
                    .Replace("{item}", itemId).Replace("{group}", group);
                foreach (var step in evidence) explanation = explanation.Replace("{" + step.Key + "}", step.Value?.ToJsonString() ?? "null");
                lines.Add(new(key, component.Id, component.Version, use.Id, use.Layer, itemId, group, Required<string>(row, "kind"),
                    row["profitRequired"]?.GetValue<bool>() ?? false, row["tax"]?.GetValue<string>() ?? "None", raw, amount, explanation, traceId, basisKeys));
            }
            if (output.Any(x => x is not JsonObject)) throw new BusinessException("OUTPUT_SHAPE", "כל שורת פלט חייבת להיות אובייקט");
            traces.Add(new(traceId, component.Id, component.Version, use.Id, template.Id, template.Version, use.Layer,
                period.From, period.To, output.Count == 0 ? "Zero" : "Calculated", context, evidence.ToArray(), tableEvidence.ToArray(), nativeTrace?.DeepClone(), component.Source));
        }
        if (items.Count == 0) traces.Add(new(Guid.NewGuid().ToString("N"), component.Id, component.Version, use.Id, template.Id, template.Version, use.Layer,
            period.From, period.To, "Zero", input, [], tableEvidence.ToArray(), nativeTrace, component.Source));
        if (lines.GroupBy(x => x.Key).Any(x => x.Count() > 1)) throw new BusinessException("DUPLICATE_LINE", "מפתחות פלט כפולים ברכיב");
        return (lines.ToArray(), traces.Select(x => x with { Assignment = applicability }).ToArray());
    }
    private static T Required<T>(JsonObject row, string key)
    {
        try { return row[key]!.GetValue<T>(); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or FormatException)
        { throw new BusinessException("OUTPUT_FIELD", $"שדה פלט חסר או שגוי: {key}"); }
    }
    // Reusable grouping adapter; financial selection, aggregation and formulas stay in ZEN.
    private static JsonArray Group(JsonArray items, string[] fields)
    {
        return new JsonArray(items.GroupBy(x => Json.Write(fields.Select(f => ConditionEvaluator.Get(x as JsonObject ?? throw new BusinessException("GROUP", "קיבוץ מחייב אובייקטים"), f)
            ?? throw new BusinessException("GROUP_FIELD", $"שדה קיבוץ חסר: {f}"))))
            .Select(g => (JsonNode)new JsonObject { ["key"] = g.Key, ["rows"] = new JsonArray(g.Select(x => x!.DeepClone()).ToArray()) }).ToArray());
    }
}
