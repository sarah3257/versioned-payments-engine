using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
namespace Payments.Api.Services;

public record CreatePlacementRequest(string RequestKey, Placement Placement);

// One initial operation: validate and calculate every month, then commit the placement and results together.
public class PlacementWorkflow(EngineDb db, WriterGate gate, PlanBuilder plans, CalculationEngine engine)
{
 public async Task<object> CreateAsync(CreatePlacementRequest request)
 {
  var p=request.Placement;
  if(p==null||string.IsNullOrWhiteSpace(request.RequestKey)||request.RequestKey.Length>64||
   string.IsNullOrWhiteSpace(p.Id)||p.Id.Length>80||!Regex.IsMatch(p.Id,@"^[\p{L}\p{N}_-]+$")||p.From==default||p.To<p.From||p.SupportLevel<=0)
   throw new BusinessException("נדרשים מזהה בקשה, מזהה השמה ללא רווחים או סימנים מיוחדים, תקופה ורמת תמיכה תקינים");
  await gate.Lock.WaitAsync();try
  {
   var matches=await db.Placements.AsNoTracking().Where(x=>x.Id==p.Id||x.CreationRequestKey==request.RequestKey).ToListAsync();
   if(matches.Count>1)throw new BusinessException("מזהה השמה ומזהה בקשה מצביעים על פעולות שונות",409);
   var existing=matches.FirstOrDefault();
   if(existing!=null)
   {
    if(existing.CreationRequestKey!=request.RequestKey||existing.Id!=p.Id||existing.ModelId!=p.ModelId||existing.Institution!=p.Institution||existing.From!=p.From||existing.To!=p.To||existing.SupportLevel!=p.SupportLevel||existing.InstitutionType!=p.InstitutionType||existing.Administration!=p.Administration||existing.Population!=p.Population)
     throw new BusinessException("מזהה השמה או בקשה כבר קיים עם פרטים אחרים",409);
    return await CreationResult(existing,true);
   }
   var institution=await db.Institutions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==p.Institution&&x.IsActive)??throw new BusinessException("מסגרת אינה קיימת או אינה פעילה");
   p.CreationRequestKey=request.RequestKey;p.InstitutionName=institution.Name;p.Ownership=institution.Ownership;
   var end=new[]{p.To,BusinessQueries.BusinessEnd(institution,p.From)}.Min();
   var calculated=new List<CalculationRun>();
   for(var month=new DateOnly(p.From.Year,p.From.Month,1);month<=end;month=month.AddMonths(1))
   {
    var a=p.From>month?p.From:month;var last=month.AddMonths(1).AddDays(-1);var b=end<last?end:last;
    decimal factor=(decimal)(b.DayNumber-a.DayNumber+1)/DateTime.DaysInMonth(month.Year,month.Month);
    var input=new CalculateRequest($"{request.RequestKey}:{month:yyyyMM}",p.ModelId,p.Institution,p.Id,a,p.Ownership,p.InstitutionType,p.SupportLevel,p.Administration,p.Population,factor);
    var plan=await plans.BuildAsync(input,false);var result=engine.Execute(plan,false);
    var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Write(input with{RequestKey=""}))));
    calculated.Add(new CalculationRun{Id=result.RunId,RequestKey=input.RequestKey,RequestHash=hash,ModelId=p.ModelId,ModelVersion=result.Header.ModelVersion,EventDate=a,Institution=p.Institution,RequestJson=JsonDefaults.Write(input),PlanJson=JsonDefaults.Write(plan),ResultJson=JsonDefaults.Write(result),CompletedAtUtc=DateTime.UtcNow,Headers=[result.Header]});
   }
   await using var tx=await db.Database.BeginTransactionAsync();
   db.Placements.Add(p);db.CalculationRuns.AddRange(calculated);
   foreach(var run in calculated)db.MonthlyRevisions.Add(new MonthlyRevision{Id=Guid.NewGuid(),PlacementId=p.Id,Month=run.Headers[0].PaymentMonth,RunId=run.Id,CreatedAtUtc=run.CompletedAtUtc});
   db.AuditEvents.Add(new AuditEvent{Action="CreatePlacementAndCalculate",Subject=p.Id,Payload=JsonDefaults.Write(request)});
   await db.SaveChangesAsync();await tx.CommitAsync();return await CreationResult(p,false);
  }finally{gate.Lock.Release();}
 }
 async Task<object> CreationResult(Placement p,bool repeated)
 {
  var institution=await db.Institutions.AsNoTracking().SingleAsync(x=>x.Id==p.Institution);
  var end=new[]{p.To,BusinessQueries.BusinessEnd(institution,p.From)}.Min();
  var keys=Enumerable.Range(0,(end.Year-p.From.Year)*12+end.Month-p.From.Month+1).Select(n=>$"{p.CreationRequestKey}:{p.From.AddMonths(n):yyyyMM}").ToList();
  var months=await db.CalculationRuns.AsNoTracking().Where(x=>keys.Contains(x.RequestKey)).SelectMany(x=>x.Headers.Select(h=>new{month=h.PaymentMonth,modelVersion=h.ModelVersion,total=h.Total,headerId=h.Id})).OrderBy(x=>x.month).ToListAsync();
  return new{placement=p,calculationEnd=end,months,repeated};
 }
 public async Task<object> SearchAsync(string? q)
 {
  q=q?.Trim();var query=db.Placements.AsNoTracking();
  if(!string.IsNullOrEmpty(q))query=query.Where(p=>p.Id.Contains(q)||p.CreationRequestKey.Contains(q)||db.CalculationRuns.Any(r=>r.RequestKey.Contains(q)&&r.Headers.Any(h=>h.PlacementId==p.Id)));
  var total=await query.CountAsync();var items=await query.OrderBy(p=>p.Id).Take(100).ToListAsync();return new{items,total};
 }
 public async Task<object> TransactionsAsync(string id)
 {
  var p=await db.Placements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id)??throw new BusinessException("השמה לא נמצאה",404);
  var headers=await db.TransactionHeaders.AsNoTracking().Include(x=>x.Details).Where(x=>x.PlacementId==id).OrderBy(x=>x.PaymentMonth).ToListAsync();
  var revisions=await db.MonthlyRevisions.AsNoTracking().Where(x=>x.PlacementId==id).ToListAsync();
  var current=revisions.GroupBy(x=>x.Month).Select(g=>g.OrderByDescending(x=>x.CreatedAtUtc).ThenByDescending(x=>x.Id).First().RunId).ToHashSet();
  var ids=headers.Select(x=>x.CalculationRunId).Distinct().ToList();var runs=await db.CalculationRuns.AsNoTracking().Where(x=>ids.Contains(x.Id)).Select(x=>new{x.Id,x.RequestKey,x.CompletedAtUtc}).ToDictionaryAsync(x=>x.Id);
  return new{placement=p,transactions=headers.Select(h=>new{header=h,isCurrent=h.EntryType!="Reversal"&&current.Contains(h.CalculationRunId),requestKey=runs.GetValueOrDefault(h.CalculationRunId)?.RequestKey,createdAt=runs.GetValueOrDefault(h.CalculationRunId)?.CompletedAtUtc,batchToken=revisions.FirstOrDefault(x=>x.RunId==h.CalculationRunId)?.BatchToken})};
 }
}
