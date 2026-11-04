using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.Services;

public class ImpactService(EngineDb db, PlanBuilder plans, CalculationEngine engine, PublicationService publications, WriterGate gate)
{
    public static async Task SeedAsync(EngineDb db)
    {
        if (await db.Placements.AnyAsync()) return;
        db.Placements.AddRange(
            new Placement { Id="demo-a-1", ModelId=1, Institution=12380, InstitutionName="מסגרת א׳", From=new(2026,1,1), To=new(2027,12,31), Ownership=Ownership.Public },
            new Placement { Id="demo-a-2", ModelId=1, Institution=12380, InstitutionName="מסגרת א׳", From=new(2026,8,15), To=new(2027,6,20), Ownership=Ownership.Public, InstitutionType="הוסטל", SupportLevel=5 },
            new Placement { Id="demo-b-1", ModelId=1, Institution=3404, InstitutionName="מסגרת ב׳", From=new(2026,1,1), To=new(2027,12,31), Ownership=Ownership.Private },
            new Placement { Id="demo-c-1", ModelId=1, Institution=5500, InstitutionName="מסגרת ג׳", From=new(2026,4,10), To=new(2027,12,31), Ownership=Ownership.Public },
            new Placement { Id="demo-c-2", ModelId=1, Institution=5500, InstitutionName="מסגרת ג׳", From=new(2026,11,10), To=new(2027,5,20), Ownership=Ownership.Public, InstitutionType="הוסטל", SupportLevel=5 },
            new Placement { Id="demo-b-2", ModelId=1, Institution=3404, InstitutionName="מסגרת ב׳", From=new(2026,7,10), To=new(2027,3,15), Ownership=Ownership.Private, InstitutionType="הוסטל" });
        await db.SaveChangesAsync();
    }

    static void Validate(ImpactRequest r)
    {
        if(r.ModelId<=0 || r.TargetVersion<=0 || r.From==default || r.To<r.From || r.From.Day!=1 || r.To.Day!=DateTime.DaysInMonth(r.To.Year,r.To.Month))
            throw new BusinessException("יש לבחור טווח חודשים מלא: מתחילת חודש ועד סוף חודש");
        if(r.To.DayNumber-r.From.DayNumber>366*5) throw new BusinessException("הסימולציה מוגבלת לחמש שנים");
        if(r.Institutions is not {Count:>0} || r.Institutions.Count>100) throw new BusinessException("יש לבחור 1–100 מסגרות");
    }

    public async Task<object> SimulateAsync(ImpactRequest r)
    {
        Validate(r);
        await gate.Lock.WaitAsync();
        try
        {
            ModelVersion? draft=null;
            if(r.Draft!=null)
            {
                if(r.Draft.ModelId!=r.ModelId || r.Draft.Version!=r.TargetVersion) throw new BusinessException("הטיוטה אינה תואמת למודל ולגרסה שנבחרו");
                draft=await publications.PublishModelAsync(r.Draft,false);
            }
            var target=draft ?? await db.ModelVersions.AsNoTracking().SingleOrDefaultAsync(x=>x.ModelId==r.ModelId&&x.Version==r.TargetVersion)
                ?? throw new BusinessException("גרסת יעד לא נמצאה");
            if(target.From>r.From || target.To<r.To) throw new BusinessException("טווח הסימולציה חייב להיות בתוך תוקף גרסת היעד");
            var placements=await db.Placements.AsNoTracking().Where(p=>p.ModelId==r.ModelId&&r.Institutions.Contains(p.Institution)).ToListAsync();
            if(r.Institutions.Except(placements.Select(p=>p.Institution)).Any()) throw new BusinessException("אחת המסגרות אינה קיימת באוכלוסיית המודל");
            var consents=await db.Consents.AsNoTracking().Where(c=>c.ModelId==r.ModelId&&r.Institutions.Contains(c.Institution)).ToListAsync();
            var lines=new List<ImpactLine>();
            foreach(var p in placements.OrderBy(p=>p.Id))
            for(var month=r.From;month<=r.To;month=month.AddMonths(1))
            {
                var end=month.AddMonths(1).AddDays(-1);
                var start=p.From>month?p.From:month; var finish=p.To<end?p.To:end;
                if(finish<start) continue;
                // Explicit POC convention: calendar-day fraction and overlap start as the determining date.
                decimal factor=(decimal)(finish.DayNumber-start.DayNumber+1)/DateTime.DaysInMonth(month.Year,month.Month);
                var request=new CalculateRequest(Guid.NewGuid().ToString(),p.ModelId,p.Institution,p.Id,start,p.Ownership,p.InstitutionType,p.SupportLevel,p.Administration,p.Population,factor);
                try
                {
                    var current=engine.Execute(await plans.BuildAsync(request,true),true);
                    var later=consents.Where(c=>c.Institution==p.Institution&&c.EffectiveFrom>r.From&&c.EffectiveFrom<=start).OrderByDescending(c=>c.EffectiveFrom).FirstOrDefault();
                    int version=later?.ModelVersion??r.TargetVersion;
                    var proposed=engine.Execute(await plans.BuildAsync(request with {SimulationModelVersion=version},true,version==draft?.Version?draft:null),true);
                    lines.Add(new(p.Id,p.Institution,month,factor,current.Header.ModelVersion,proposed.Header.ModelVersion,current.Header.Total,proposed.Header.Total,proposed.Header.Total-current.Header.Total,null));
                }
                catch(BusinessException e){lines.Add(new(p.Id,p.Institution,month,factor,null,null,null,null,null,e.Message));}
            }
            var good=lines.Where(x=>x.Error==null).ToList();
            return new {simulation=true, baseline="חישוב לפי ההגדרות וההסכמות הנוכחיות, לא סכומים ששולמו", partial=good.Count!=lines.Count,
                placementCount=lines.Select(x=>x.PlacementId).Distinct().Count(), monthCount=lines.Count, errorCount=lines.Count-good.Count,
                increase=good.Where(x=>x.Difference>0).Sum(x=>x.Difference??0), decrease=-good.Where(x=>x.Difference<0).Sum(x=>x.Difference??0), net=good.Sum(x=>x.Difference??0),
                byInstitution=good.GroupBy(x=>x.Institution).Select(g=>new {institution=g.Key,current=g.Sum(x=>x.CurrentAmount??0),proposed=g.Sum(x=>x.ProposedAmount??0),difference=g.Sum(x=>x.Difference??0)}), rows=lines,
                note="ללא שמירת תשלומים או הסכמות. תחולה בתחילת חודש; חודש חלקי לפי ימים קלנדריים ותאריך קובע בתחילת החפיפה — הנחת POC לאישור. הסכמות מאוחרות קיימות נשמרות. אין ביצוע החזרים או קיזוזים."};
        }
        finally {gate.Lock.Release();}
    }

    public async Task<object> SaveConsentsAsync(ConsentBatch r, PublishModel? draft=null)
    {
        if(r.Institutions is not {Count:>0} || r.Institutions.Count>100 || r.EffectiveFrom==default || r.EffectiveFrom.Day!=1) throw new BusinessException("נדרשות מסגרות ותאריך תחולה בתחילת חודש");
        await gate.Lock.WaitAsync();
        try
        {
            await using var transaction=await db.Database.BeginTransactionAsync();
            if(draft!=null)
            {
                if(draft.ModelId!=r.ModelId||draft.Version!=r.TargetVersion) throw new BusinessException("גרסת הטיוטה אינה תואמת");
                await publications.PublishModelAsync(draft);
            }
            var model=await db.ModelVersions.SingleOrDefaultAsync(x=>x.ModelId==r.ModelId&&x.Version==r.TargetVersion)
                ?? throw new BusinessException("המודל לא פורסם");
            if(r.EffectiveFrom<model.From||r.EffectiveFrom>model.To) throw new BusinessException("התחולה מחוץ לתוקף המודל");
            int added=0;
            foreach(int institution in r.Institutions.Distinct())
            {
                if(!await db.Placements.AnyAsync(p=>p.Institution==institution&&p.ModelId==r.ModelId)) throw new BusinessException("מסגרת לא נמצאה");
                var existing=await db.Consents.SingleOrDefaultAsync(c=>c.Institution==institution&&c.ModelId==r.ModelId&&c.EffectiveFrom==r.EffectiveFrom);
                if(existing!=null) {if(existing.ModelVersion!=r.TargetVersion) throw new BusinessException("קיימת הסכמה לגרסה אחרת באותו תאריך",409); continue;}
                db.Consents.Add(new Consent {Institution=institution,ModelId=r.ModelId,ModelVersion=r.TargetVersion,EffectiveFrom=r.EffectiveFrom,RecordedAtUtc=DateTime.UtcNow}); added++;
            }
            await db.SaveChangesAsync();await transaction.CommitAsync();
            return new {added, note="ההסכמות נרשמו לחישובים חדשים. לא נשמרו תשלומים חדשים, לא שונו תוצאות עבר ולא בוצעו קיזוזים."};
        }
        finally {gate.Lock.Release();}
    }
}
public record PublishImpactRequest(ConsentBatch Consent, PublishModel Draft);
