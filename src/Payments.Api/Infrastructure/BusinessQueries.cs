using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
namespace Payments.Api.Infrastructure;
public static class BusinessQueries
{
 public static async Task<List<PriceVersion>> PricesAsync(this EngineDb db,DateOnly? date=null)
 {
  var m=db.MaintenancePrices.AsNoTracking(); var h=db.HumanPrices.AsNoTracking();
  if(date!=null){m=m.Where(x=>x.From<=date.Value&&date.Value<=x.To);h=h.Where(x=>x.From<=date.Value&&date.Value<=x.To);}
  var a=await m.ToListAsync();var b=await h.ToListAsync();
  return a.Select(x=>new PriceVersion{Id=x.Id,PriceKey=x.PriceKey,Version=x.Version,Description=x.ItemName,Kind=ComponentKind.Maintenance,From=x.From,To=x.To,Amount=x.Amount,UnitMeasureId=x.UnitMeasureId}).Concat(b.Select(x=>new PriceVersion{Id=x.Id,PriceKey=x.PriceKey,Version=x.Version,Description=x.JobTypeName,Kind=ComponentKind.Staff,From=x.From,To=x.To,Amount=x.MonthlyAmount})).ToList();
 }
 public static async Task<List<ComponentVersion>> ComponentsAsync(this EngineDb db,int[]? ids=null)
 {
  var q=db.ComponentVersions.AsNoTracking();if(ids!=null)q=q.Where(c=>ids.Contains(c.ComponentId));
  var items=await q.Include(c=>c.MaintenanceItems).Include(c=>c.HumanItems).Include(c=>c.Rows).ThenInclude(r=>r.Conditions).AsSplitQuery().ToListAsync();
  foreach(var c in items)c.Items=c.MaintenanceItems.Select(i=>new ComponentItem{Id=i.Id,ComponentVersionId=i.ComponentVersionId,PriceKey=i.PriceKey,Factor=i.PaymentPercent/100,ProfitRequired=i.IsProfitRequired,TaxLiability=i.TaxCalcId}).Concat(c.HumanItems.Select(i=>new ComponentItem{Id=i.Id,ComponentVersionId=i.ComponentVersionId,PriceKey=i.PriceKey,Factor=i.TotalCountPosition,ProfitRequired=i.IsProfitRequired,TaxLiability=i.TaxCalcId})).ToList();
  return items;
 }
 public static DateOnly BusinessEnd(Institution institution,DateOnly start)
 {
  int day=Math.Min(institution.BusinessYearEndDay,DateTime.DaysInMonth(start.Year,institution.BusinessYearEndMonth));
  var end=new DateOnly(start.Year,institution.BusinessYearEndMonth,day);
  if(end<start){int year=start.Year+1;end=new(year,institution.BusinessYearEndMonth,Math.Min(institution.BusinessYearEndDay,DateTime.DaysInMonth(year,institution.BusinessYearEndMonth)));}
  return end;
 }
}
