using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.Services;

public class PlanBuilder(EngineDb db)
{
    public async Task<ExecutionPlan> BuildAsync(CalculateRequest request, bool simulation, ModelVersion? draft = null)
    {
        if (request.ModelId <= 0 || request.Institution <= 0 || string.IsNullOrWhiteSpace(request.PlacementId) || request.EventDate == default ||
            request.MonthlyFactor is < 0 or > 1 || request.SupportLevel <= 0 || !Enum.IsDefined(request.Ownership))
            throw new BusinessException("נתוני השמה חסרים או מקדם חודשי אינו בטווח 0–1");
        if (!simulation && request.SimulationModelVersion != null)
            throw new BusinessException("בחירת מודל ידנית מותרת בסימולציה בלבד");
        // All registry reads use one transaction/snapshot. No lazy loads during execution.
        await db.Database.OpenConnectionAsync();
        await using var native = ((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);
        await using var transaction = await db.Database.UseTransactionAsync(native);
        var models = await db.ModelVersions.AsNoTracking().Include(x => x.Components).Where(x => x.ModelId == request.ModelId).ToListAsync();
        var actions = (await db.VersionActions.AsNoTracking().ToListAsync()).GroupBy(x=>new{x.EntityType,x.BusinessId,x.Version}).Select(g=>g.OrderByDescending(x=>x.Id).First()).ToList();
        models = models.Where(m=>!actions.Any(a=>a.EntityType=="Model"&&a.BusinessId==m.ModelId&&a.Version==m.Version&&(a.Disabled||a.ClosedAfter<request.EventDate))).ToList();
        var consents = await db.Consents.AsNoTracking().Where(x => x.ModelId == request.ModelId && x.Institution == request.Institution).ToListAsync();
        int? version = simulation ? request.SimulationModelVersion : null;
        if (version == null)
        {
            var consent = consents.Where(c => c.EffectiveFrom <= request.EventDate && models.Any(m=>m.Version==c.ModelVersion)).OrderByDescending(c => c.EffectiveFrom).ThenByDescending(c=>c.ModelVersion).FirstOrDefault();
            // Explicit POC policy: initial model version is baseline before first consent.
            var activations = await db.RetroBatches.AsNoTracking().Where(x => x.ModelId == request.ModelId).ToListAsync();
            var active = activations.Where(x => models.Any(m=>m.Version==x.ModelVersion) && x.From <= request.EventDate && request.EventDate <= x.To && (string.IsNullOrWhiteSpace(x.InstitutionsJson) || JsonDefaults.Read<List<int>>(x.InstitutionsJson).Contains(request.Institution))).OrderByDescending(x => x.ModelVersion).FirstOrDefault();
            version = active?.ModelVersion ?? consent?.ModelVersion ?? models.OrderBy(m => m.Version).FirstOrDefault()?.Version;
        }
        if (draft != null) { if (!simulation || draft.ModelId != request.ModelId) throw new BusinessException("טיוטה מותרת בסימולציה בלבד"); models.Add(draft); }
        var model = models.SingleOrDefault(m => m.Version == version && m.From <= request.EventDate && request.EventDate <= m.To)
            ?? throw new BusinessException("אין גרסת מודל מאושרת ובתוקף לתאריך האירוע");
        var ids = model.Components.Select(c => c.ComponentId).ToArray();
        var definitions = (await db.ComponentsAsync(ids)).Where(c=>!actions.Any(a=>a.EntityType=="Component"&&a.BusinessId==c.ComponentId&&a.Version==c.Version&&(a.Disabled||a.ClosedAfter<request.EventDate))).ToList();
        var selected = new List<SelectedComponent>();
        foreach (var link in model.Components.OrderBy(l => l.Layer).ThenBy(l => l.Order))
        {
            var candidates = definitions.Where(c => c.ComponentId == link.ComponentId && c.From <= request.EventDate && request.EventDate <= c.To)
                .OrderByDescending(c => c.Version).ToList();
            var c = candidates.FirstOrDefault() ?? throw new BusinessException($"אין גרסה בתוקף לרכיב {link.ComponentId}");
            // Choose version first, then evaluate that version's association. Do not fall back to an older matching version.
            if (c.SupportLevel != null && c.SupportLevel != request.SupportLevel ||
                c.InstitutionType != null && c.InstitutionType != request.InstitutionType ||
                c.Administration != null && c.Administration != request.Administration ||
                c.Population != null && c.Population != request.Population) continue;
            selected.Add(new(c, link.Layer, link.Order, $"מתוך {candidates.Count} גרסאות בתוקף נבחר המספר הגבוה: {c.Version}; תנאי השיוך התקיימו"));
        }
        var allPrices = await db.PricesAsync(request.EventDate);
        var prices = allPrices.Where(p => p.From <= request.EventDate && request.EventDate <= p.To)
            .GroupBy(p => new{p.Kind,p.PriceKey}).Select(g => g.OrderByDescending(p => p.Version).First()).ToList();
        var allTax = await db.TaxRateVersions.AsNoTracking().ToListAsync();
        var tax = allTax.Where(t => t.Ownership == request.Ownership && t.From <= request.EventDate && request.EventDate <= t.To)
            .GroupBy(t => t.SourceKind).Select(g => g.OrderByDescending(t => t.Version).First()).ToList();
        var allAgreements = await db.AgreementVersions.AsNoTracking().Where(a => a.Institution == request.Institution).ToListAsync();
        var agreements = allAgreements.Where(a => a.From <= request.EventDate && request.EventDate <= a.To)
            .OrderByDescending(a => a.Version).Take(1).ToList();
        await native.CommitAsync();
        return new(request, model.Version, model.Name, selected, prices, tax, agreements, DateTime.UtcNow);
    }
}
