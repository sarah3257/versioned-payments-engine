using System.Text.Json.Nodes;
using Rules.Application.Rules;
using Rules.Domain;

namespace Rules.Application.Publication;

public static class DefinitionValidator
{
    public static void Fields(FieldDefinition[] fields, JsonObject values, string context)
    {
        foreach (var field in fields)
        {
            var value = ConditionEvaluator.Get(values, field.Name);
            if (value is null && !field.Required) continue;
            if (!Matches(field.Type, value)) throw new BusinessException("FIELD_TYPE", $"{context}: שדה {field.Name} חייב להיות {field.Type}");
        }
    }
    public static bool Matches(string type, JsonNode? value) => type switch
    {
        "number" => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "date" => value is JsonValue v && v.TryGetValue<string>(out var s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", out _),
        "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
        "array" => value is JsonArray,
        "object" => value is JsonObject,
        _ => false
    };
    public static EngineState Overlay(EngineState original, PublicationPackage package)
    {
        var s = Json.Copy(original);
        Require(!string.IsNullOrWhiteSpace(package.RequestId) && !string.IsNullOrWhiteSpace(package.Publisher), "נדרשים מזהה פרסום ומפרסם");
        foreach (var t in package.Templates)
        {
            Unique(s.Templates.Any(x => x.Id == t.Id && x.Version == t.Version));
            Require(t.Version > 0 && t.Steps.Length <= 40 && t.Steps.Select(x => x.Key).Distinct().Count() == t.Steps.Length, "תבנית לא תקינה");
            foreach (var step in t.Steps) { Identifier(step.Key); ExpressionPolicy.Validate(step.Expression); }
            ExpressionPolicy.Validate(t.OutputExpression);
            if (t.ForEach is not null) ExpressionPolicy.Validate(t.ForEach);
            foreach (var field in t.Parameters.Concat(t.Facts)) { Path(field.Name); Require(new[] { "number", "string", "date", "boolean", "array", "object" }.Contains(field.Type), "טיפוס שדה לא נתמך"); }
            foreach (var key in t.GroupBy ?? []) Path(key);
            s.Templates.Add(t);
        }
        foreach (var c in package.Components)
        {
            Unique(s.Components.Any(x => x.Id == c.Id && x.Version == c.Version));
            Dates(c.From, c.To); Require(c.Version > 0 && !string.IsNullOrWhiteSpace(c.Source), "נדרשים גרסה ועוגן נורמטיבי");
            var template = s.Templates.SingleOrDefault(x => x.Id == c.TemplateId && x.Version == c.TemplateVersion)
                ?? throw new BusinessException("TEMPLATE_REFERENCE", $"תבנית אינה קיימת: {c.TemplateId}/{c.TemplateVersion}");
            Fields(template.Parameters, c.Parameters, c.Id);
            foreach (var collection in template.Collections ?? [])
            {
                var items = ConditionEvaluator.Get(c.Parameters, collection.Path) as JsonArray
                    ?? throw new BusinessException("PARAMETER_COLLECTION", "אוסף פרמטרים אינו מערך");
                Require(items.Count <= 1000, "אוסף פרמטרים גדול מדי");
                foreach (var item in items)
                {
                    Require(item is JsonObject, "פריטי מפרט חייבים להיות אובייקטים");
                    Fields(collection.Fields, item!.AsObject(), c.Id + "/" + collection.Path);
                }
            }
            if (s.Components.Any(x => x.Id == c.Id && x.Version >= c.Version)) throw new BusinessException("VERSION_ORDER", "גרסה חדשה חייבת להיות גבוהה מהגרסאות הקיימות");
            var overlaps = s.Components.Where(x => x.Id == c.Id && x.From <= c.To && x.To >= c.From).ToArray();
            if (overlaps.Length > 0) Require(c.Supersedes is not null && overlaps.Any(x => x.Version == c.Supersedes), "בחפיפת רכיב נדרש supersedes מפורש");
            if (c.Table is not null) Table(c.Table, template);
            s.Components.Add(c);
        }
        foreach (var r in package.References)
        {
            Unique(s.References.Any(x => x.Id == r.Id && x.Version == r.Version)); Dates(r.From, r.To);
            Require(r.Version > 0 && !string.IsNullOrWhiteSpace(r.Source), "נתון ייחוס חסר גרסה או מקור");
            if (s.References.Any(x => x.Id == r.Id && x.Version >= r.Version)) throw new BusinessException("VERSION_ORDER", "סדר גרסאות נתון ייחוס אינו תקין");
            var overlaps = s.References.Where(x => x.Id == r.Id && x.From <= r.To && x.To >= r.From).ToArray();
            if (overlaps.Length > 0) Require(r.Supersedes is not null && overlaps.Any(x => x.Version == r.Supersedes), "בחפיפת נתון ייחוס נדרש supersedes");
            s.References.Add(r);
        }
        foreach (var m in package.Models)
        {
            Unique(s.Models.Any(x => x.Id == m.Id && x.Version == m.Version)); Dates(m.From, m.To);
            Require(m.Version > 0 && m.Components.Length is > 0 and <= 100 && !string.IsNullOrWhiteSpace(m.Agreement) && !string.IsNullOrWhiteSpace(m.Source), "הגדרת מודל אינה תקינה");
            Require(m.Components.Select(x => x.Id).Distinct().Count() == m.Components.Length, "מזהי שיוך כפולים");
            if (s.Models.Any(x => x.Id == m.Id && x.Version >= m.Version)) throw new BusinessException("VERSION_ORDER", "גרסת מודל חדשה חייבת להיות גבוהה");
            var overlaps = s.Models.Where(x => x.Id == m.Id && x.Agreement == m.Agreement && x.From <= m.To && x.To >= m.From).ToArray();
            if (overlaps.Length > 0) Require(m.Supersedes is not null && overlaps.Any(x => x.Version == m.Supersedes), "גרסאות מודל חופפות באותו הסכם מחייבות supersedes");
            foreach (var use in m.Components)
            {
                Require(use.Layer > 0 && use.Layer <= 100, "שכבה אינה תקינה");
                foreach (var condition in use.Conditions) ConditionEvaluator.Validate(condition);
                Coverage(s.Components.Where(x => x.Id == use.ComponentId).Select(x => (x.From, x.To)), m.From, m.To, use.ComponentId);
            }
            s.Models.Add(m);
        }
        foreach (var agreement in package.Agreements)
        {
            Dates(agreement.From, agreement.To);
            var previous = s.Agreements.Where(x => x.InstitutionId == agreement.InstitutionId && x.ModelId == agreement.ModelId).ToArray();
            Require(agreement.Version > 0 && previous.All(x => x.Version < agreement.Version), "גרסת הסכמה אינה חדשה");
            var overlaps = previous.Where(x => x.From <= agreement.To && x.To >= agreement.From).ToArray();
            if (overlaps.Length > 0) Require(agreement.Supersedes is not null && overlaps.Any(x => x.Version == agreement.Supersedes), "הסכמה חופפת מחייבת החלפה מפורשת");
            Coverage(s.Models.Where(x => x.Id == agreement.ModelId && x.Agreement == agreement.Agreement).Select(x => (x.From, x.To)), agreement.From, agreement.To, "הסכם");
            s.Agreements.Add(agreement);
        }
        // Validate affected existing models too, not only models carried by this package.
        foreach (var m in s.Models)
            foreach (var use in m.Components)
                Coverage(s.Components.Where(x => x.Id == use.ComponentId).Select(x => (x.From, x.To)), m.From, m.To, use.ComponentId);
        foreach (var c in s.Components.Where(x => x.Table is not null))
        {
            var template = s.Templates.Single(x => x.Id == c.TemplateId && x.Version == c.TemplateVersion);
            foreach (var row in c.Table!.Rows)
                foreach (var output in template.TableOutputs ?? [])
                    if (output.Role == "reference" && row.Outputs[output.Name] is { } value)
                        Coverage(s.References.Where(x => x.Id == value.GetValue<string>()).Select(x => (x.From, x.To)), c.From, c.To, value.GetValue<string>());
        }
        foreach (var c in s.Components)
        {
            var template = s.Templates.Single(x => x.Id == c.TemplateId && x.Version == c.TemplateVersion);
            foreach (var collection in template.Collections ?? [])
                foreach (var item in (JsonArray)ConditionEvaluator.Get(c.Parameters, collection.Path)!)
                    foreach (var field in collection.ReferenceFields)
                    {
                        var id = ConditionEvaluator.Get(item!.AsObject(), field)!.GetValue<string>();
                        Coverage(s.References.Where(x => x.Id == id).Select(x => (x.From, x.To)), c.From, c.To, id);
                    }
        }
        return s;
    }
    private static void Table(DecisionTable table, ComponentTemplate template)
    {
        Require(table.HitPolicy is "unique" or "collect" or "first" && table.NoMatch is "zero" or "error", "מדיניות טבלה אינה תקינה");
        Require(table.Rows.Length is > 0 and <= 1000 && table.Columns.Length is > 0 and <= 30, "גודל טבלה אינו תקין");
        Require(table.Columns.Select(x => x.Id).Distinct().Count() == table.Columns.Length && table.Rows.Select(x => x.Id).Distinct().Count() == table.Rows.Length, "מזהים כפולים בטבלה");
        foreach (var column in table.Columns)
        {
            Identifier(column.Id); Require(!column.Id.StartsWith("out_", StringComparison.Ordinal), "מזהה עמודה משתמש בקידומת שמורה");
            Require(template.Facts.Any(x => x.Name == column.Field && x.Type == column.Type), $"עמודת תנאי אינה תואמת לחוזה האירוע: {column.Field}");
        }
        foreach (var row in table.Rows)
        {
            Require(!string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.Source), "נדרשים מזהה ועוגן לשורת טבלה");
            Require(row.Conditions.Count == table.Columns.Length && table.Columns.All(x => row.Conditions.ContainsKey(x.Id)), "נדרש תנאי מפורש לכל עמודה, כולל any");
            foreach (var column in table.Columns)
            {
                var c = row.Conditions[column.Id]; ConditionEvaluator.Validate(c);
                Require(c.Field == column.Field, "שדה התנאי אינו תואם לעמודה");
                if (c.Operator is "any" or "missing") continue;
                if (c.Operator == "in") Require(c.Value is JsonArray a && a.All(x => Matches(column.Type, x)), "ערכי רשימה אינם תואמים לטיפוס העמודה");
                else Require(Matches(column.Type, c.Value), "ערך תנאי אינו תואם לטיפוס העמודה");
                if (c.Operator == "between") Require(Matches(column.Type, c.To), "קצה טווח אינו תואם לטיפוס העמודה");
            }
        }
        var keys = table.Rows[0].Outputs.Select(x => x.Key).Order().ToArray();
        Require(table.Rows.All(x => x.Outputs.Select(v => v.Key).Order().SequenceEqual(keys)), "מבנה פלט הטבלה חייב להיות אחיד");
        Require(template.TableOutputs is { Length: > 0 }, "תבנית טבלת החלטה חייבת להגדיר תפקידי עמודות פלט");
        Require(keys.SequenceEqual(template.TableOutputs!.Select(x => x.Name).Order()), "עמודות פלט אינן תואמות לתבנית הסוג");
        foreach (var row in table.Rows)
            foreach (var output in template.TableOutputs!)
            {
                Identifier(output.Name); Require(output.Name is not ("row" or "rowId"), "שם פלט שמור למזהה שורת ההחלטה");
                Require(new[] { "value", "reference", "multiplier", "input", "override" }.Contains(output.Role), "תפקיד עמודת פלט אינו נתמך");
                var value = row.Outputs[output.Name];
                if (value is null && !output.Required) continue;
                Require(Matches(output.Type, value), $"טיפוס פלט אינו תקין: {output.Name}");
                if (output.Role == "input") Require(template.Facts.Any(x => x.Name == value!.GetValue<string>() && x.Type == "number"), "הפניית כמות חייבת להצביע לשדה מספרי מוכר");
            }
    }
    public static void Coverage(IEnumerable<(DateOnly From, DateOnly To)> intervals, DateOnly from, DateOnly to, string label)
    {
        var next = from;
        foreach (var item in intervals.OrderBy(x => x.From))
        {
            if (item.To < next) continue;
            if (item.From > next) break;
            if (item.To >= to) return;
            next = item.To.AddDays(1);
        }
        throw new BusinessException("VALIDITY_GAP", $"חסר כיסוי תוקף עבור {label} ב־{next}");
    }
    private static void Dates(DateOnly from, DateOnly to) => Require(from <= to && to < DateOnly.MaxValue, "תאריכי תוקף לא תקינים");
    private static void Identifier(string key) => Require(System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9_]*$"), "מזהה צעד לא תקין");
    private static void Path(string path) { foreach (var part in path.Split('.')) Identifier(part); }
    private static void Unique(bool exists) { if (exists) throw new BusinessException("IMMUTABLE_VERSION", "גרסה קיימת אינה ניתנת לשינוי או לפרסום חוזר בתוכן אחר", 409); }
    private static void Require(bool condition, string message) { if (!condition) throw new BusinessException("DEFINITION", message); }
}
