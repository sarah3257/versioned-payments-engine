using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
namespace Payments.Api.Services;

public class RetroService(EngineDb db, PlanBuilder plans, CalculationEngine engine, PublicationService publications, WriterGate gate, PaymentService payments)
{
    public record Line(string PlacementId,int Institution,string InstitutionName,DateOnly Month,int? BeforeVersion,int? AfterVersion,decimal? Before,decimal? After,decimal? Difference,bool WasStored,string? Error,string PaymentStatus="Pending",string Treatment="ReplacePlanned");
    record Work(Placement Placement,DateOnly Month,ExecutionPlan Plan,CalculationResult Before,CalculationResult After,MonthlyRevision? Previous,TransactionHeader? Source);
    record Preview(List<Line> Rows,List<Work> Work,string? Token);
    static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static CalculateRequest Request(Placement p,DateOnly month)
    {
        var end=month.AddMonths(1).AddDays(-1); var start=p.From>month?p.From:month; var finish=p.To<end?p.To:end;
        var fraction=(decimal)(finish.DayNumber-start.DayNumber+1)/DateTime.DaysInMonth(month.Year,month.Month);
        return new("retro-preview",p.ModelId,p.Institution,p.Id,start,p.Ownership,p.InstitutionType,p.SupportLevel,p.Administration,p.Population,fraction);
    }
        static DateOnly PeriodEnd(Placement p,PublishModel draft,Institution? institution)
    {
        var yearEnd=draft.EndCommitment ?? BusinessQueries.BusinessEnd(institution??new Institution(),draft.From);
        return new[]{draft.To,p.To,yearEnd}.Min();
    }
    static IEnumerable<DateOnly> Months(Placement p,DateOnly from,DateOnly to)
    {
        for(var month=new DateOnly(from.Year,from.Month,1);month<=to;month=month.AddMonths(1))
            if(p.From<=month.AddMonths(1).AddDays(-1)&&p.To>=month)yield return month;
    }
    void Store(ExecutionPlan plan,CalculationResult result,MonthlyRevision? previous,string? token)
    {
        string key="retro-"+result.RunId;
        var request=plan.Request with {RequestKey=key,SimulationModelVersion=null};
        plan=plan with {Request=request};
        db.CalculationRuns.Add(new CalculationRun {Id=result.RunId,RequestKey=key,RequestHash=Hash(JsonDefaults.Write(request with {RequestKey=""})),ModelId=request.ModelId,ModelVersion=plan.ModelVersion,EventDate=request.EventDate,Institution=request.Institution,RequestJson=JsonDefaults.Write(request),PlanJson=JsonDefaults.Write(plan),ResultJson=JsonDefaults.Write(result),CompletedAtUtc=DateTime.UtcNow,Headers=[result.Header]});
        db.MonthlyRevisions.Add(new MonthlyRevision {Id=Guid.NewGuid(),PlacementId=request.PlacementId,Month=result.Header.PaymentMonth,RunId=result.RunId,PreviousId=previous?.Id,BatchToken=token,CreatedAtUtc=DateTime.UtcNow});
    }
    public async Task SeedAsync()
    {
        if(await db.MonthlyRevisions.AnyAsync())return;
        var placements=await db.Placements.AsNoTracking().ToListAsync();
        var work=new List<(ExecutionPlan Plan,CalculationResult Result)>();
        foreach(var p in placements)
        foreach(var month in Months(p,new(2026,7,1),new(2026,12,31)))
        {
            var plan=await plans.BuildAsync(Request(p,month),true);work.Add((plan,engine.Execute(plan,false)));
        }
        await using var transaction=await db.Database.BeginTransactionAsync();
        foreach(var item in work)Store(item.Plan,item.Result,null,null);
        await db.SaveChangesAsync();await transaction.CommitAsync();
    }
    async Task<Preview> BuildAsync(PublishModel draft)
    {
        if(draft.From.Day!=1||draft.To.Day!=DateTime.DaysInMonth(draft.To.Year,draft.To.Month)||draft.To.DayNumber-draft.From.DayNumber>366*5)
            throw new BusinessException("יש לבחור תחולה מתחילת חודש עד סוף חודש, עד חמש שנים");
        var candidate=await publications.PublishModelAsync(draft,false);
        if(draft.Institutions is {Count:0})throw new BusinessException("יש לבחור מסגרת אחת לפחות");
        var institutions=await db.Institutions.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        if(draft.Institutions!=null && draft.Institutions.Any(x=>!institutions.ContainsKey(x)))throw new BusinessException("מסגרת לא קיימת");
        var placements=await db.Placements.AsNoTracking().Where(x=>x.ModelId==draft.ModelId && (draft.Institutions==null || draft.Institutions.Contains(x.Institution))).OrderBy(x=>x.Id).ToListAsync();
        if(placements.Count==0)throw new BusinessException("אין השמות למודל שנבחר");
        var revisions=await db.MonthlyRevisions.AsNoTracking().ToListAsync();
        var runs=await db.CalculationRuns.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        var headers=await db.TransactionHeaders.ToDictionaryAsync(x=>x.Id);
        var rows=new List<Line>();var work=new List<Work>();
        foreach(var p in placements)
        foreach(var month in Months(p,draft.From,PeriodEnd(p,draft, institutions.GetValueOrDefault(p.Institution))))
        {
            var request=Request(p,month);
            var previous=revisions.Where(x=>x.PlacementId==p.Id&&x.Month==month).OrderByDescending(x=>x.CreatedAtUtc).ThenByDescending(x=>x.Id).FirstOrDefault();
            try
            {
                var source=previous!=null?headers.Values.Single(x=>x.CalculationRunId==previous.RunId && x.EntryType!="Reversal"):null;
                var before=previous!=null?JsonDefaults.Read<CalculationResult>(runs[previous.RunId].ResultJson):engine.Execute(await plans.BuildAsync(request,true),true);
                var plan=await plans.BuildAsync(request with {SimulationModelVersion=draft.Version},true,candidate);
                var after=engine.Execute(plan,false);
                rows.Add(new(p.Id,p.Institution,p.InstitutionName,month,before.Header.ModelVersion,after.Header.ModelVersion,before.Header.Total,after.Header.Total,after.Header.Total-before.Header.Total,previous!=null,null,source?.PaymentStatus??"Pending",source?.PaymentStatus=="Paid"?"ReversePaidAndRecalculate":"ReplacePlanned"));
                work.Add(new(p,month,plan,before,after,previous,source));
            }
            catch(BusinessException e){rows.Add(new(p.Id,p.Institution,p.InstitutionName,month,null,null,null,null,null,previous!=null,e.Message));}
        }
        // Include full deterministic inputs, selection and previous revision, not random execution IDs.
        var fingerprint=new {draft,placements,items=work.Select(w=>new {w.Month,w.Placement.Id,Previous=w.Previous?.Id,Before=w.Before.Header.Total,PaymentStatus=w.Source?.PaymentStatus,Plan=w.Plan with {SelectedAtUtc=DateTime.MinValue}})};
        string? token=rows.Count>0&&rows.All(x=>x.Error==null)?Hash(JsonDefaults.Write(fingerprint)):null;
        return new(rows,work,token);
    }
    static object Summary(Preview p)=>new {previewToken=p.Token,canExecute=p.Token!=null,placementCount=p.Rows.Select(x=>x.PlacementId).Distinct().Count(),monthCount=p.Rows.Count,errorCount=p.Rows.Count(x=>x.Error!=null),increase=p.Rows.Where(x=>x.Difference>0).Sum(x=>x.Difference??0),decrease=-p.Rows.Where(x=>x.Difference<0).Sum(x=>x.Difference??0),net=p.Rows.Sum(x=>x.Difference??0),rows=p.Rows};
    public async Task<object> SimulateAsync(PublishModel draft)
    {
        await gate.Lock.WaitAsync();try{return Summary(await BuildAsync(draft));}finally{gate.Lock.Release();}
    }
    public async Task<object> ExecuteAsync(RetroExecution request,bool failBeforeCommit=false)
    {
        if(string.IsNullOrWhiteSpace(request.PreviewToken))throw new BusinessException("יש להריץ סימולציה לפני הביצוע");
        await gate.Lock.WaitAsync();
        try
        {
            var old=await db.RetroBatches.AsNoTracking().SingleOrDefaultAsync(x=>x.Token==request.PreviewToken);
            if(old!=null)
            {
                if(old.RequestJson!=JsonDefaults.Write(request.Draft))throw new BusinessException("הבקשה שונה מהביצוע המקורי",409);
                return JsonDefaults.Read<System.Text.Json.JsonElement>(old.ResultJson);
            }
            var preview=await BuildAsync(request.Draft);
            if(preview.Token==null||preview.Token!=request.PreviewToken)throw new BusinessException("הנתונים השתנו או שיש שגיאות. הריצו סימולציה מחדש",409);
            await using var transaction=await db.Database.BeginTransactionAsync();
            foreach(var item in preview.Work.Where(x=>x.Source!=null))
                if(await db.TransactionHeaders.AsNoTracking().Where(x=>x.Id==item.Source!.Id).Select(x=>x.PaymentStatus).SingleAsync()!=item.Source!.PaymentStatus)throw new BusinessException("סטטוס תשלום השתנה; יש לבצע סימולציה מחדש",409);
                        foreach(var item in preview.Work)
            {
                var latest=await db.MonthlyRevisions.AsNoTracking().Where(x=>x.PlacementId==item.Placement.Id&&x.Month==item.Month).OrderByDescending(x=>x.CreatedAtUtc).ThenByDescending(x=>x.Id).Select(x=>(Guid?)x.Id).FirstOrDefaultAsync();
                if(latest!=item.Previous?.Id)throw new BusinessException("תוצאה חודשית השתנתה במקביל; יש להריץ סימולציה מחדש",409);
            }
            await publications.PublishModelAsync(request.Draft);
                        int reversed=0;
            foreach(var w in preview.Work)
            {
                w.After.Header.ReplacedHeaderId=w.Source?.Id;
                Store(w.Plan,w.After,w.Previous,request.PreviewToken);
                if(w.Source!=null)
                {
                    if(w.Source.PaymentStatus=="Paid")
                    {
                        var reverse=JsonDefaults.Read<TransactionHeader>(JsonDefaults.Write(w.Before.Header));
                        reverse.Id=Guid.NewGuid();reverse.CalculationRunId=w.After.RunId;reverse.Total=-w.Before.Header.Total;
                        reverse.EntryType="Reversal";reverse.ReversesHeaderId=w.Source.Id;reverse.ReplacedHeaderId=null;
                        reverse.PaymentStatus="Pending";reverse.PaidAtUtc=null;reverse.Status="Pending";
                        foreach(var d in reverse.Details){d.TransactionHeaderId=reverse.Id;d.Amount=-d.Amount;d.Explanation="ביטול תשלום מקור "+w.Source.Id+": "+d.Explanation;}
                        db.TransactionHeaders.Add(reverse);reversed++;
                    }
                    else payments.Supersede(w.Source,w.After.Header.Id);
                }
            }
            if(request.Draft.Institutions!=null)
            foreach(int institution in request.Draft.Institutions.Distinct())
            {
                var existing=await db.Consents.SingleOrDefaultAsync(x=>x.Institution==institution && x.ModelId==request.Draft.ModelId && x.EffectiveFrom==request.Draft.From && x.ModelVersion==request.Draft.Version);
                
                if(existing==null)db.Consents.Add(new Consent{Institution=institution,ModelId=request.Draft.ModelId,ModelVersion=request.Draft.Version,EffectiveFrom=request.Draft.From,RecordedAtUtc=DateTime.UtcNow});
            }
            var response=new {executed=true,modelVersion=request.Draft.Version,updatedMonths=preview.Work.Count,reversalHeaders=reversed,net=preview.Rows.Sum(x=>x.Difference??0),token=request.PreviewToken,note="עודכנו התוצאות המתוכננות; לתוצאות ששולמו נוצרו מינוס מקור וחישוב חדש. המקור נשמר להיסטוריה. לא הועבר כסף לבנק."};
            db.RetroBatches.Add(new RetroBatch {Token=request.PreviewToken,InstitutionsJson=request.Draft.Institutions==null?"":JsonDefaults.Write(request.Draft.Institutions),RequestJson=JsonDefaults.Write(request.Draft),ResultJson=JsonDefaults.Write(response),ModelId=request.Draft.ModelId,ModelVersion=request.Draft.Version,From=request.Draft.From,To=request.Draft.To,CreatedAtUtc=DateTime.UtcNow});
            await db.SaveChangesAsync();
            if(failBeforeCommit)throw new BusinessException("כשל בדיקה לפני שמירה סופית");
            await transaction.CommitAsync();return response;
        }
        finally{gate.Lock.Release();}
    }
    public async Task<object> CurrentAsync()
    {
        var headers=await db.TransactionHeaders.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        var revisions=await db.MonthlyRevisions.AsNoTracking().ToListAsync();
        var runs=await db.CalculationRuns.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        return revisions.GroupBy(x=>new{x.PlacementId,x.Month}).Select(g=>
        {
            var current=g.OrderByDescending(x=>x.CreatedAtUtc).ThenByDescending(x=>x.Id).First();
            var result=JsonDefaults.Read<CalculationResult>(runs[current.RunId].ResultJson);
            var header=headers.Values.Single(x=>x.CalculationRunId==current.RunId && x.EntryType!="Reversal");
            return new {current.PlacementId,current.Month,current.RunId,headerId=header.Id,paymentStatus=header.PaymentStatus,modelVersion=result.Header.ModelVersion,total=result.Header.Total,revisions=g.Count(),current.PreviousId};
        }).OrderBy(x=>x.PlacementId).ThenBy(x=>x.Month).ToList();
    }
}
