using Rules.Application.Execution;
using Rules.Application.Publication;
using Rules.Application.Retro;
using Rules.Domain;

namespace Rules.Application;

public sealed class EngineService(IEngineStore store, CalculationRunner runner, DecisionTableAnalyzer analyzer, IRuleRuntime runtime)
{
    public EngineState State() => store.Read().State;
    public Task<CalculationResult> Simulate(CalculationRequest request) => runner.Calculate(store.Read().State, request, simulation: true);
    public async Task<PublicationPreview> Preview(PublicationPreviewRequest request)
    {
        if (request.Cases.Length is < 1 or > 30) throw new BusinessException("PREVIEW_CASES", "נדרשים 1–30 מקרי סימולציה לפרסום");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = store.Read(); var state = snapshot.State;
            if (state.Publications.Any(x => x.RequestId == request.Package.RequestId)) throw new BusinessException("ALREADY_PUBLISHED", "מזהה הפרסום כבר פורסם", 409);
            var overlay = DefinitionValidator.Overlay(state, request.Package);
            foreach (var template in request.Package.Templates)
            {
                foreach (var step in template.Steps) runtime.ValidateExpression(step.Expression);
                runtime.ValidateExpression(template.OutputExpression);
                if (template.ForEach is not null) runtime.ValidateExpression(template.ForEach);
            }
            foreach (var component in request.Package.Components.Where(x => x.Table is not null))
            {
                var template = overlay.Templates.Single(x => x.Id == component.TemplateId && x.Version == component.TemplateVersion);
                analyzer.Validate(component.Table!, template.Facts.Where(x => !x.Required).Select(x => x.Name).ToHashSet());
            }
            var results = new List<CalculationResult>();
            foreach (var sample in request.Cases) results.Add(await runner.Calculate(overlay, sample, simulation: true));
            var affectedComponents = request.Package.Components.Select(x => x.Id).ToHashSet();
            if (request.Package.References.Length > 0)
                affectedComponents.UnionWith(overlay.Components.Select(x => x.Id)); // Conservative: references can be selected dynamically by expressions.
            var affected = overlay.Models.Where(x => x.Components.Any(c => affectedComponents.Contains(c.ComponentId)) || request.Package.Models.Any(m => m.Id == x.Id))
                .Select(x => x.Id).Distinct().Order().ToArray();
            var preview = new PublicationPreview(Guid.NewGuid().ToString("N"), Json.CatalogHash(state), Json.PositionHash(state), request.Package, request.Cases,
                results.ToArray(), results.SelectMany(x => ResultComparer.Compare(state, x)).ToArray(), DateTimeOffset.UtcNow.AddMinutes(30), affected);
            state.Previews.RemoveAll(x => x.ExpiresAt < DateTimeOffset.UtcNow); state.Previews.Add(preview);
            if (store.TryCommit(snapshot.Revision, state)) return preview;
        }
        throw Conflict();
    }
    public PublicationReceipt Publish(string token)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = store.Read(); var state = snapshot.State;
            var preview = state.Previews.SingleOrDefault(x => x.Token == token) ?? throw new BusinessException("PREVIEW_NOT_FOUND", "סימולציית פרסום לא נמצאה", 404);
            var hash = Json.Hash(preview.Package);
            var receipt = state.Publications.SingleOrDefault(x => x.RequestId == preview.Package.RequestId);
            if (receipt is not null)
            {
                if (receipt.Hash != hash) throw new BusinessException("REQUEST_CONFLICT", "מזהה פרסום כבר קיים בתוכן אחר", 409);
                return receipt;
            }
            if (preview.ExpiresAt < DateTimeOffset.UtcNow || preview.CatalogHash != Json.CatalogHash(state) || preview.PositionHash != Json.PositionHash(state))
                throw new BusinessException("STALE_PREVIEW", "הנתונים השתנו או הסימולציה פגה; יש להריץ סימולציה חדשה", 409);
            var overlay = DefinitionValidator.Overlay(state, preview.Package);
            receipt = new(preview.Package.RequestId, hash, DateTimeOffset.UtcNow, preview.Package.Publisher);
            overlay.Publications.Add(receipt); overlay.Audit.Add(new("Publish", receipt.Publisher, receipt.PublishedAt, receipt.RequestId));
            if (store.TryCommit(snapshot.Revision, overlay)) return receipt;
        }
        throw Conflict();
    }
    public async Task<CalculationResult> Execute(CalculationRequest request)
    {
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var snapshot = store.Read(); var state = snapshot.State; var hash = Json.Hash(request);
                var existing = state.Runs.SingleOrDefault(x => x.Result.Input.EventId == request.EventId);
                if (existing is not null)
                {
                    if (existing.RequestHash != hash) throw new BusinessException("REQUEST_CONFLICT", "מזהה אירוע כבר קיים בתוכן אחר", 409);
                    return existing.Result;
                }
                var prior = state.Runs.Where(x => x.Result.Input.PlacementId == request.PlacementId).Select(x => x.Result.Input).ToArray();
                if (request.SourceRevision < 1 || prior.Any(x => x.SourceRevision >= request.SourceRevision))
                    throw new BusinessException("STALE_EVENT", "גרסת נתוני ההשמה אינה חדשה; יש להרכיב קלט עדכני במערכת המקור", 409);
                if (prior.Any(x => x.InstitutionId != request.InstitutionId || x.ModelId != request.ModelId))
                    throw new BusinessException("PLACEMENT_IDENTITY", "לא ניתן לשנות מסגרת או מודל באותו מזהה השמה ב־POC; נדרש תהליך מעבר מפורש");
                var result = await runner.Calculate(state, request);
                ResultComparer.Apply(state, result); state.Runs.Add(new(result, hash));
                state.Audit.Add(new("Calculate", "payments", DateTimeOffset.UtcNow, request.EventId));
                if (store.TryCommit(snapshot.Revision, state)) return result;
                // Recalculate against the new snapshot if another event or settlement won the race.
            }
            throw Conflict();
        }
        catch (BusinessException e)
        {
            RecordFailure(request.EventId, e); throw;
        }
    }
    public void Settle(string placementId, DateOnly month, string expectedRunId, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new BusinessException("ACTOR", "נדרש מזהה מבצע");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = store.Read(); var state = snapshot.State;
            var position = state.Positions.SingleOrDefault(x => x.PlacementId == placementId && x.Month == month)
                ?? throw new BusinessException("POSITION", "חודש השמה לא נמצא", 404);
            if (position.RunId != expectedRunId) throw new BusinessException("STALE_POSITION", "החודש השתנה מאז הצגתו; יש לרענן", 409);
            var settled = state.Settlements.SelectMany(x => x.LedgerIds).ToHashSet();
            var ids = state.Ledger.Where(x => x.PlacementId == placementId && x.Month == month && !settled.Contains(x.Id)).Select(x => x.Id).ToArray();
            if (ids.Length == 0) return;
            state.Settlements.Add(new(Guid.NewGuid().ToString("N"), ids, actor, DateTimeOffset.UtcNow));
            state.Positions.Remove(position); state.Positions.Add(position with { Paid = true });
            state.Audit.Add(new("SettleDemo", actor, DateTimeOffset.UtcNow, placementId + "/" + month));
            if (store.TryCommit(snapshot.Revision, state)) return;
        }
        throw Conflict();
    }
    private void RecordFailure(string eventId, BusinessException error)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = store.Read(); snapshot.State.Failures.Add(new(eventId, error.Code, error.Message, DateTimeOffset.UtcNow));
            if (store.TryCommit(snapshot.Revision, snapshot.State)) return;
        }
    }
    private static BusinessException Conflict() => new("CONCURRENCY", "אירוע מקביל שינה את הנתונים; ניתן לנסות שוב עם אותו מזהה", 409);
}
