using Rules.Application.Versioning;
using Rules.Domain;

namespace Rules.Application.Execution;

public sealed class CalculationRunner(ComponentExecutor executor, IRuleRuntime runtime)
{
    public const string EngineVersion = "zen-payments-poc/1.0";
    public async Task<CalculationResult> Calculate(EngineState state, CalculationRequest request, bool simulation = false)
    {
        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.PlacementId) || string.IsNullOrWhiteSpace(request.InstitutionId))
            throw new BusinessException("EVENT_ID", "נדרשים מזהי אירוע, השמה ומסגרת");
        if (!simulation && request.ForcedAgreement is not null) throw new BusinessException("SIMULATION_ONLY", "בחירת הסכם כפויה מותרת רק בסימולציה");
        var segments = PeriodPlanner.Plan(state, request);
        var results = new List<(DateOnly Month, SegmentResult Result)>();
        foreach (var segment in segments)
        {
            var facts = request.Periods.Where(x => x.From <= segment.From && x.To >= segment.To).ToArray();
            if (facts.Length != 1) throw new BusinessException("FACT_COVERAGE", $"נדרשת תמונת נתונים אחת למקטע {segment.From}–{segment.To}");
            var model = VersionResolver.Model(state, request, segment.From);
            var references = VersionResolver.References(state, segment.From);
            var lines = new List<PaymentLine>(); var traces = new List<ComponentTrace>();
            foreach (var layer in model.Components.GroupBy(x => x.Layer).OrderBy(x => x.Key))
            {
                var previous = lines.ToArray();
                var layerLines = new List<PaymentLine>();
                foreach (var use in layer.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    var component = VersionResolver.Component(state, use.ComponentId, segment.From);
                    var template = state.Templates.Single(x => x.Id == component.TemplateId && x.Version == component.TemplateVersion);
                    var output = await executor.Execute(component, template, use, segment, facts[0].Facts, references, previous);
                    layerLines.AddRange(output.Lines); traces.AddRange(output.Traces);
                }
                lines.AddRange(layerLines);
            }
            var selection = new System.Text.Json.Nodes.JsonObject
            {
                ["date"] = Json.Node(segment.From),
                ["modelSource"] = model.Source,
                ["agreement"] = Json.Node(state.Agreements.Where(x => x.InstitutionId == request.InstitutionId && x.ModelId == request.ModelId && x.From <= segment.From && x.To >= segment.From).OrderByDescending(x => x.Version).FirstOrDefault()),
                ["reason"] = request.ForcedAgreement is null ? "הסכם מסגרת היסטורי; גרסת מודל גבוהה בתוקף בתוך אותו הסכם" : "הסכם שנבחר לסימולציה בלבד"
            };
            results.Add((segment.Month, new(segment.From, segment.To, model.Id, model.Version, model.Agreement, lines.ToArray(), traces.ToArray(), selection)));
        }
        return new(Guid.NewGuid().ToString("N"), Json.CatalogHash(state), EngineVersion, runtime.Version, request,
            results.GroupBy(x => x.Month).Select(x => new MonthResult(x.Key, x.Select(v => v.Result).ToArray())).ToArray(), DateTimeOffset.UtcNow);
    }
}
