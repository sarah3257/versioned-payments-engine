using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.Services;

// SQLite POC: one process-wide writer. DB uniqueness also protects request keys.
public sealed class WriterGate { public SemaphoreSlim Lock { get; } = new(1, 1); }

public class RunService(EngineDb db, PlanBuilder plans, CalculationEngine engine, WriterGate gate, PaymentService payments)
{
    public async Task<CalculationResult> CalculateAsync(CalculateRequest request, bool simulation = false, bool failBeforeCommit = false)
    {
        if (string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 150)
            throw new BusinessException("נדרש RequestKey ייחודי באורך עד 150");
        var normalized = request with { RequestKey = "" };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Write(normalized))));
        if(simulation)return engine.Execute(await plans.BuildAsync(request,true),true);
        await gate.Lock.WaitAsync();
        try
        {
            if (!simulation)
            {
                var existing = await db.CalculationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.RequestKey == request.RequestKey);
                if (existing != null)
                {
                    if (existing.RequestHash != hash && JsonDefaults.Write(JsonDefaults.Read<CalculateRequest>(existing.RequestJson) with {RequestKey=""}) != JsonDefaults.Write(normalized)) throw new BusinessException("אותו RequestKey נשלח עם תוכן שונה", 409);
                    return JsonDefaults.Read<CalculationResult>(existing.ResultJson);
                }
            }
            var plan = await plans.BuildAsync(request, simulation);
            var result = engine.Execute(plan, simulation);
            if (simulation) return result;
            // Only final results are persisted, in a single DB transaction.
            await using var transaction = await db.Database.BeginTransactionAsync();
                        if(await db.Placements.AnyAsync(p=>p.Id==request.PlacementId))
            {
                var previous=await db.MonthlyRevisions.Where(x=>x.PlacementId==request.PlacementId&&x.Month==result.Header.PaymentMonth).OrderByDescending(x=>x.CreatedAtUtc).ThenByDescending(x=>x.Id).FirstOrDefaultAsync();
                if(previous!=null)
                {
                    var old=await db.TransactionHeaders.SingleAsync(x=>x.CalculationRunId==previous.RunId&&x.EntryType!="Reversal");
                    if(old.PaymentStatus=="Paid")throw new BusinessException("קיים תשלום ששולם לחודש; יש להשתמש בסימולציה ובביצוע רטרואקטיבי");
                    result.Header.ReplacedHeaderId=old.Id;payments.Supersede(old,result.Header.Id);
                }
                db.MonthlyRevisions.Add(new MonthlyRevision{Id=Guid.NewGuid(),PlacementId=request.PlacementId,Month=result.Header.PaymentMonth,RunId=result.RunId,PreviousId=previous?.Id,CreatedAtUtc=DateTime.UtcNow});
            }
            var run = new CalculationRun { Id = result.RunId, RequestKey = request.RequestKey, RequestHash = hash,
                ModelId = request.ModelId, ModelVersion = result.Header.ModelVersion, EventDate = request.EventDate,
                Institution = request.Institution, RequestJson = JsonDefaults.Write(request), PlanJson = JsonDefaults.Write(plan),
                ResultJson = JsonDefaults.Write(result), CompletedAtUtc = DateTime.UtcNow, Headers = [result.Header] };
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            if (failBeforeCommit) throw new BusinessException("כשל בדיקה לפני COMMIT: כל התוצאה מבוטלת");
            await transaction.CommitAsync();
            return result;
        }
        finally { gate.Lock.Release(); }
    }

    public async Task<object> ReplayAsync(Guid runId)
    {
        var stored = await db.CalculationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId)
            ?? throw new BusinessException("ריצה לא נמצאה", 404);
        if (stored.EngineVersion != "engine-v1") throw new BusinessException("גרסת מנוע השחזור אינה מותקנת");
        var plan = JsonDefaults.Read<ExecutionPlan>(stored.PlanJson);
        var original = JsonDefaults.Read<CalculationResult>(stored.ResultJson);
        var replay = engine.Execute(plan, false, original.RunId, original.Header.Id);
        replay.Header.ReplacedHeaderId=original.Header.ReplacedHeaderId; replay.Header.ReversesHeaderId=original.Header.ReversesHeaderId; replay.Header.EntryType=original.Header.EntryType;
        var same = JsonDefaults.Write(original) == JsonDefaults.Write(replay);
        return new { matchesOriginal = same, original, replay, note = "שחזור מתמונת הנתונים המקורית; לא חישוב מחדש לפי מדיניות עדכנית ולא נוצרת רשומת תשלום נוספת" };
    }
}
