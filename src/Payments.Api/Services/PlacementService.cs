using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
namespace Payments.Api.Services;
public class PlacementService(EngineDb db,WriterGate gate)
{
 public async Task<Placement> AddAsync(Placement p)
 {
  if(string.IsNullOrWhiteSpace(p.Id)||p.From==default||p.To<p.From||p.SupportLevel<=0)throw new BusinessException("נדרשים מזהה השמה, תאריכים ורמת תמיכה תקינים");
  await gate.Lock.WaitAsync();try
  {
   if(await db.Placements.AnyAsync(x=>x.Id==p.Id))throw new BusinessException("מזהה השמה קיים",409);
   var institution=await db.Institutions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==p.Institution&&x.IsActive)??throw new BusinessException("מסגרת לא קיימת או אינה פעילה");
   if(!await db.ModelVersions.AnyAsync(x=>x.ModelId==p.ModelId))throw new BusinessException("מודל לא קיים");
   p.InstitutionName=institution.Name;p.Ownership=institution.Ownership;
   db.Placements.Add(p);db.AuditEvents.Add(new AuditEvent{Action="CreatePlacement",Subject=p.Id,Payload=JsonDefaults.Write(p)});await db.SaveChangesAsync();return p;
  }finally{gate.Lock.Release();}
 }
 public async Task<Institution> SaveInstitutionAsync(Institution r)
 {
  if(r.Id<=0||string.IsNullOrWhiteSpace(r.Name)||r.BusinessYearEndMonth is <1 or >12||r.BusinessYearEndDay<1||r.BusinessYearEndDay>DateTime.DaysInMonth(2000,r.BusinessYearEndMonth)||!Enum.IsDefined(r.Ownership))throw new BusinessException("פרטי מסגרת או סוף שנה עסקית אינם תקינים");
  await gate.Lock.WaitAsync();try{var existing=await db.Institutions.FindAsync(r.Id);if(existing==null)db.Institutions.Add(r);else db.Entry(existing).CurrentValues.SetValues(r);db.AuditEvents.Add(new AuditEvent{Action="SaveInstitution",Subject=r.Id.ToString(),Payload=JsonDefaults.Write(r)});await db.SaveChangesAsync();return r;}finally{gate.Lock.Release();}
 }
}
