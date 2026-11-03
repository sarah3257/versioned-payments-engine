using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.Services;

// SQLite POC: one process-wide writer. DB uniqueness also protects request keys.
public sealed class WriterGate { public SemaphoreSlim Lock { get; } = new(1, 1); }

public class RunService(EngineDb db, PlanBuilder plans, CalculationEngine engine, WriterGate gate)
{
    public async Task<CalculationResult> CalculateAsync(CalculateRequest request, bool simulation = false, bool failBeforeCommit = false)
    {
        if (string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 150)
            throw new BusinessException("נדרש RequestKey ייחודי באורך עד 150");
        var normalized = request with { RequestKey = "" };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Write(normalized))));
        await gate.Lock.WaitAsync();
        try
        {
            if (!simulation)
            {
                var existing = await db.CalculationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.RequestKey == request.RequestKey);
                if (existing != null)
                {
                    if (existing.RequestHash != hash) throw new BusinessException("אותו RequestKey נשלח עם תוכן שונה", 409);
                    return JsonDefaults.Read<CalculationResult>(existing.ResultJson);
                }
            }
            var plan = await plans.BuildAsync(request, simulation);
            var result = engine.Execute(plan, simulation);
            if (simulation) return result;
            // Only final results are persisted, in a single DB transaction.
            await using var transaction = await db.Database.BeginTransactionAsync();
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
        var same = JsonDefaults.Write(original) == JsonDefaults.Write(replay);
        return new { matchesOriginal = same, original, replay, note = "שחזור מתמונת הנתונים המקורית; לא חישוב מחדש לפי מדיניות עדכנית ולא נוצרת רשומת תשלום נוספת" };
    }
}
