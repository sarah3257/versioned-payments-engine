using System.Globalization;
using Payments.Api.Domain;

namespace Payments.Api.Services;

public interface IComponentCalculator
{
    ComponentKind Kind { get; }
    void Calculate(SelectedComponent component, CalculationContext context);
}

public class CalculationContext(ExecutionPlan plan, TransactionHeader header, List<TraceEntry> trace)
{
    public ExecutionPlan Plan { get; } = plan;
    public TransactionHeader Header { get; } = header;
    public List<TraceEntry> Trace { get; } = trace;
    public PriceVersion Price(string key, ComponentKind kind=ComponentKind.Maintenance) => Plan.Prices.SingleOrDefault(p => p.PriceKey == key && p.Kind==kind)
        ?? throw new BusinessException($"מחיר בתוקף לא נמצא: {key}");
    public void Add(SelectedComponent s, string itemKey, string description, decimal amount, bool profit, TaxLiability tax, string explanation)
    {
        if (s.Definition.TemplateVersion != "v1") throw new BusinessException("גרסת מימוש חישוב אינה מותקנת");
        Header.Details.Add(new TransactionDetail { TransactionHeaderId = Header.Id, ComponentId = s.Definition.ComponentId,
            ComponentVersion = s.Definition.Version, TemplateVersion = s.Definition.TemplateVersion, Layer = s.Layer,
            Kind = s.Definition.Kind, ItemKey = itemKey, Description = description,
            Amount = decimal.Round(amount, 3, MidpointRounding.AwayFromZero), ProfitRequired = profit,
            TaxLiability = tax, Explanation = explanation });
    }
}

public class ItemCalculator(ComponentKind kind) : IComponentCalculator
{
    public ComponentKind Kind => kind;
    public void Calculate(SelectedComponent s, CalculationContext c)
    {
        foreach (var item in s.Definition.Items.OrderBy(i => i.PriceKey, StringComparer.Ordinal))
        {
            var price = c.Price(item.PriceKey,Kind);
            if (price.Kind != Kind) throw new BusinessException("סוג המחירון אינו מתאים לרכיב");
            if(price.UnitMeasureId!=1)throw new BusinessException("ה־POC תומך בחישוב פריט חודשי בלבד; יחידת המידה אינה חודשית");
            var amount = price.Amount * item.Factor * c.Plan.Request.MonthlyFactor;
            c.Add(s, item.PriceKey, price.Description, amount, item.ProfitRequired, item.TaxLiability,
                $"מחיר {price.Amount} (שורת מחירון {price.Id}, גרסה {price.Version}) × מקדם {item.Factor} × חלק חודש {c.Plan.Request.MonthlyFactor}");
        }
    }
}
public class BasketCalculator : IComponentCalculator
{
    public ComponentKind Kind => ComponentKind.Basket;
    public void Calculate(SelectedComponent s, CalculationContext c)
    {
        foreach (var row in s.Definition.Rows.OrderBy(r => r.RowKey, StringComparer.Ordinal))
        {
            var checks = row.Conditions.Select(condition => (condition, match: Matches(condition, c.Plan.Request))).ToList();
            bool matches = checks.All(x => x.match);
            c.Trace.Add(new(s.Definition.ComponentId, s.Definition.Version, s.Layer,
                $"שורת החלטה {row.RowKey}: {(matches ? "התאימה" : "לא התאימה")}; " + string.Join("; ", checks.Select(x => $"{x.condition.Attribute} {x.condition.Operator} {x.condition.Value ?? "*"}: {x.match}"))));
            if (!matches) continue;
            var price = row.ResultAmount == 0 ? c.Price(row.PriceKey!) : null;
            var amount = row.ResultAmount == 0 ? price!.Amount : row.ResultAmount;
            c.Add(s, "row:" + row.RowKey, row.Description, amount * c.Plan.Request.MonthlyFactor, false, row.TaxLiability,
                $"שורת החלטה {row.RowKey}; כל התנאים התקיימו; בסיס {amount}; " +
                (price == null ? "סכום קבוע" : $"מחירון {price.PriceKey} גרסה {price.Version}") + $" × חלק חודש {c.Plan.Request.MonthlyFactor}");
        }
    }
    public static bool Matches(DecisionCondition condition, CalculateRequest request)
    {
        if (condition.Value == null) return true; // Explicit wildcard, not SQL NULL comparison.
        string actual = condition.Attribute switch
        {
            "institutionType" => request.InstitutionType, "supportLevel" => request.SupportLevel.ToString(CultureInfo.InvariantCulture),
            "institution" => request.Institution.ToString(CultureInfo.InvariantCulture), "ownership" => request.Ownership.ToString(),
            "age" => request.BirthDate is {} birth ? (request.EventDate.Year-birth.Year-(birth.AddYears(request.EventDate.Year-birth.Year)>request.EventDate?1:0)).ToString(CultureInfo.InvariantCulture) : throw new BusinessException("חסר תאריך לידה לחישוב גיל"),
            "gender" => request.GenderId?.ToString(CultureInfo.InvariantCulture) ?? throw new BusinessException("חסר מגדר"),
            "administration" => request.Administration, "population" => request.Population,
            _ => throw new BusinessException("מאפיין החלטה לא נתמך")
        };
        if (condition.Operator == "=") return string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
        if (condition.Operator == "!=") return !string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
        var left = decimal.Parse(actual, CultureInfo.InvariantCulture);
        var right = decimal.Parse(condition.Value, CultureInfo.InvariantCulture);
        return condition.Operator switch { "<" => left < right, ">" => left > right, "<=" => left <= right,
            ">=" => left >= right, "BETWEEN" => left >= right && left <= decimal.Parse(condition.ValueTo!, CultureInfo.InvariantCulture),
            _ => throw new BusinessException("אופרטור לא נתמך") };
    }
}
public class ProfitCalculator : IComponentCalculator
{
    public ComponentKind Kind => ComponentKind.Profit;
    public void Calculate(SelectedComponent s, CalculationContext c)
    {
        var basis = c.Header.Details.Where(d => d.Layer < 6 && d.ProfitRequired).Sum(d => d.Amount);
        var agreement = s.Definition.UseAgreement ? c.Plan.Agreements.SingleOrDefault() : null;
        if (s.Definition.UseAgreement && agreement == null) throw new BusinessException("אין הסכם רווח בתוקף למסגרת");
        var rate = agreement?.ProfitPercent ?? s.Definition.ProfitPercent;
        c.Add(s, "profit", "רווח", basis * rate / 100, false, s.Definition.ProfitTaxLiability,
            $"בסיס חבות רווח משכבות 1–5: {basis} × {rate}%" + (agreement == null ? " (קבוע)" : $" (הסכם גרסה {agreement.Version})"));
    }
}
public class TaxCalculator : IComponentCalculator
{
    public ComponentKind Kind => ComponentKind.Tax;
    public void Calculate(SelectedComponent s, CalculationContext c)
    {
        bool Liable(TransactionDetail d) => d.TaxLiability == TaxLiability.Both ||
            d.TaxLiability == (c.Plan.Request.Ownership == Ownership.Private ? TaxLiability.Private : TaxLiability.Public);
        var groups = c.Header.Details.Where(d => d.Layer < 7 && Liable(d)).GroupBy(d => d.Kind).ToList();
        if (groups.Count == 0) c.Add(s, "tax:none", "מס", 0, false, TaxLiability.None, "אין בסיס חייב במס");
        foreach (var group in groups)
        {
            var tax = c.Plan.TaxRates.SingleOrDefault(t => t.SourceKind == group.Key)
                ?? throw new BusinessException($"אין שיעור מס בתוקף עבור {group.Key} ובעלות {c.Plan.Request.Ownership}");
            var basis = group.Sum(d => d.Amount);
            c.Add(s, "tax:" + group.Key, tax.TaxName, basis * tax.Percent / 100, false, TaxLiability.None,
                $"בסיס {basis} מסוג {group.Key} משכבות 1–6 × {tax.Percent}%; שיעור מס גרסה {tax.Version}");
        }
    }
}

public class CalculationEngine(IEnumerable<IComponentCalculator> calculators)
{
    public CalculationResult Execute(ExecutionPlan plan, bool simulation, Guid? runId = null, Guid? headerId = null)
    {
        var id = runId ?? Guid.NewGuid();
        var header = new TransactionHeader { Id = headerId ?? Guid.NewGuid(), CalculationRunId = id,
            PlacementId = plan.Request.PlacementId, Institution = plan.Request.Institution,
            ModelId = plan.Request.ModelId, ModelVersion = plan.ModelVersion,
            PaymentMonth = new DateOnly(plan.Request.EventDate.Year, plan.Request.EventDate.Month, 1), Status = simulation ? "Simulation" : "Pending" };
        var trace = new List<TraceEntry>();
        var context = new CalculationContext(plan, header, trace);
        foreach (var selected in plan.Components.OrderBy(c => c.Layer).ThenBy(c => c.Order))
        {
            if (selected.Definition.TemplateVersion != "v1") throw new BusinessException("גרסת תבנית ישנה אינה זמינה במנוע הנוכחי");
            trace.Add(new(selected.Definition.ComponentId, selected.Definition.Version, selected.Layer, selected.SelectionReason));
            try { calculators.Single(c => c.Kind == selected.Definition.Kind).Calculate(selected, context); }
            catch(BusinessException e) { throw new BusinessException($"רכיב {selected.Definition.ComponentId} ({selected.Definition.Name}), שכבה {selected.Layer}: {e.Message}",e.Status){ErrorCode="COMPONENT_FAILED",ComponentId=selected.Definition.ComponentId,Layer=selected.Layer}; }
        }
        header.Total = header.Details.Sum(d => d.Amount);
        return new(id, header, trace, simulation);
    }
}
