using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
namespace Payments.Api.Services;
public record VersionCommand(string EntityType,int BusinessId,int Version,bool Disabled,DateOnly? ClosedAfter,string Reason);
public record ForecastRequest(string RequestKey,string PlacementId,DateOnly From,bool Simulation=true,int Offset=0,int PageSize=12);
public class ManagementService(EngineDb db,WriterGate gate,RunService runs)
{
 public async Task<object> ChangeVersionAsync(VersionCommand r)
 {
  if(r.EntityType is not ("Model" or "Component")||string.IsNullOrWhiteSpace(r.Reason))throw new BusinessException("נדרשים סוג ישות וסיבת שינוי");
  await gate.Lock.WaitAsync();try
  {
   bool exists=r.EntityType=="Model"?await db.ModelVersions.AnyAsync(x=>x.ModelId==r.BusinessId&&x.Version==r.Version):await db.ComponentVersions.AnyAsync(x=>x.ComponentId==r.BusinessId&&x.Version==r.Version);
   if(!exists)throw new BusinessException("גרסה לא נמצאה",404);
   db.VersionActions.Add(new VersionAction{EntityType=r.EntityType,BusinessId=r.BusinessId,Version=r.Version,Disabled=r.Disabled,ClosedAfter=r.ClosedAfter,Reason=r.Reason});
   db.AuditEvents.Add(new AuditEvent{Action="VersionState",Subject=$"{r.EntityType}/{r.BusinessId}/{r.Version}",Payload=JsonDefaults.Write(r)});await db.SaveChangesAsync();return new{success=true};
  }finally{gate.Lock.Release();}
 }
 public async Task<object> WhereUsedAsync(int id)
 {
  var models=await db.ModelVersions.AsNoTracking().Include(x=>x.Components).Where(x=>x.Components.Any(c=>c.ComponentId==id)).ToListAsync();
  var modelIds=models.Select(x=>x.ModelId).Distinct().ToArray();
  return new {componentId=id,models=models.Select(x=>new{x.ModelId,x.Version,x.From,x.To}),placements=await db.Placements.AsNoTracking().Where(x=>modelIds.Contains(x.ModelId)).ToListAsync()};
 }
 public async Task<object> ForecastAsync(ForecastRequest r)
 {
  if(string.IsNullOrWhiteSpace(r.RequestKey)||r.RequestKey.Length>80||r.From==default||r.PageSize is <1 or >24||r.Offset<0)throw new BusinessException("בקשת תחזית לא תקינה");
  var p=await db.Placements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==r.PlacementId)??throw new BusinessException("השמה לא נמצאה",404);
  var institution=await db.Institutions.AsNoTracking().SingleAsync(x=>x.Id==p.Institution);
  var start=r.From>p.From?r.From:p.From;var end=new[]{p.To,BusinessQueries.BusinessEnd(institution,start)}.Min();
  var months=new List<DateOnly>();for(var m=new DateOnly(start.Year,start.Month,1);m<=end;m=m.AddMonths(1))months.Add(m);
  var items=new List<object>();int next=r.Offset;
  foreach(var month in months.Skip(r.Offset).Take(r.PageSize))
  {
   var a=start>month?start:month;var b=end<month.AddMonths(1).AddDays(-1)?end:month.AddMonths(1).AddDays(-1);
   decimal factor=(decimal)(b.DayNumber-a.DayNumber+1)/DateTime.DaysInMonth(month.Year,month.Month);
   var request=new CalculateRequest($"{r.RequestKey}:{p.Id}:{month:yyyyMM}",p.ModelId,p.Institution,p.Id,a,p.Ownership,p.InstitutionType,p.SupportLevel,p.Administration,p.Population,factor);
   try{items.Add(new{month,result=await runs.CalculateAsync(request,r.Simulation)});next++;}
   catch(BusinessException e){return new {completed=false,nextOffset=next,items,error=e.Message,end};}
  }
  return new {completed=next>=months.Count,nextOffset=next,items,error=(string?)null,end};
 }
}
