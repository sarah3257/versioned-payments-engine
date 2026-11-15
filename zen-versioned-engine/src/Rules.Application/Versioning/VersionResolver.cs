using Rules.Domain;

namespace Rules.Application.Versioning;

public static class VersionResolver
{
    public static ModelVersion Model(EngineState state, CalculationRequest request, DateOnly date)
    {
        var agreements = state.Agreements.Where(x => x.InstitutionId == request.InstitutionId && x.ModelId == request.ModelId && x.From <= date && x.To >= date).ToArray();
        var agreement = request.ForcedAgreement;
        if (agreement is null)
        {
            if (agreements.Length == 0) throw new BusinessException("AGREEMENT_COVERAGE", $"נדרש הסכם למסגרת בתאריך {date}");
            agreement = agreements.OrderByDescending(x => x.Version).First().Agreement;
        }
        var versions = state.Models.Where(x => x.Id == request.ModelId && x.Agreement == agreement && x.From <= date && x.To >= date).OrderByDescending(x => x.Version).ToArray();
        return versions.FirstOrDefault() ?? throw new BusinessException("MODEL_COVERAGE", $"אין מודל {request.ModelId} להסכם {agreement} בתאריך {date}");
    }
    public static ComponentVersion Component(EngineState state, string id, DateOnly date) =>
        state.Components.Where(x => x.Id == id && x.From <= date && x.To >= date).OrderByDescending(x => x.Version).FirstOrDefault()
        ?? throw new BusinessException("COMPONENT_COVERAGE", $"אין גרסת רכיב {id} בתאריך {date}");
    public static ReferenceVersion[] References(EngineState state, DateOnly date) => state.References
        .Where(x => x.From <= date && x.To >= date).GroupBy(x => x.Id)
        .Select(x => x.OrderByDescending(v => v.Version).First()).OrderBy(x => x.Id).ToArray();
}
