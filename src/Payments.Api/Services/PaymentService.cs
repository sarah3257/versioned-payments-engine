using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
namespace Payments.Api.Services;
public record ChangePaymentStatus(string Status,string ExpectedStatus,string Reason="עריכה לצורך POC");
public class PaymentService(EngineDb db,WriterGate gate)
{
 public async Task<object> ChangeAsync(Guid id,ChangePaymentStatus r)
 {
  if(r.Status is not ("Pending" or "Paid"))throw new BusinessException("סטטוס מותר: ממתין לתשלום או שולם");
  await gate.Lock.WaitAsync();try
  {
   await using var tx=await db.Database.BeginTransactionAsync();
   var header=await db.TransactionHeaders.SingleOrDefaultAsync(x=>x.Id==id)??throw new BusinessException("כותרת לא נמצאה",404);
   if(header.EntryType=="Reversal"||header.PaymentStatus=="Superseded" || await db.TransactionHeaders.AnyAsync(x=>x.ReversesHeaderId==id))throw new BusinessException("לא ניתן לערוך תוצאה מוחלפת או ביטול");
   if(header.PaymentStatus!=r.ExpectedStatus)throw new BusinessException("הסטטוס השתנה; יש לרענן",409);
   if(header.PaymentStatus!=r.Status)
   {
    db.PaymentStatusEvents.Add(new PaymentStatusEvent{HeaderId=id,PreviousStatus=header.PaymentStatus,NewStatus=r.Status,ChangedAtUtc=DateTime.UtcNow,Reason=r.Reason});
    header.PaymentStatus=r.Status;header.PaidAtUtc=r.Status=="Paid"?DateTime.UtcNow:null;
    await db.SaveChangesAsync();
   }
   await tx.CommitAsync();return new {header.Id,header.PaymentStatus,header.PaidAtUtc};
  }finally{gate.Lock.Release();}
 }
 public static async Task SeedInstitutionsAsync(EngineDb db)
 {
  foreach(var (id,name,owner) in new[]{(12380,"מסגרת א׳",Ownership.Public),(3404,"מסגרת ב׳",Ownership.Private),(5500,"מסגרת ג׳",Ownership.Public)})
   if(!await db.Institutions.AnyAsync(x=>x.Id==id))db.Institutions.Add(new Institution{Id=id,Name=name,Ownership=owner,BusinessYearEndMonth=12,BusinessYearEndDay=31});
  await db.SaveChangesAsync();
 }
 public void Supersede(TransactionHeader old,Guid replacement)
 {
  if(old.PaymentStatus=="Paid")return;
  db.PaymentStatusEvents.Add(new PaymentStatusEvent{HeaderId=old.Id,PreviousStatus=old.PaymentStatus,NewStatus="Superseded",ChangedAtUtc=DateTime.UtcNow,Reason="הוחלף בעקבות חישוב מחדש: "+replacement});old.PaymentStatus="Superseded";
 }
}
