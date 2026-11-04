using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;
using Xunit;
namespace Payments.Tests;
public class AccountingHistoryTests
{
 [Fact] public async Task TwoRetroChangesReverseEachPaidHeaderOnlyOnce()
 {
  using var app=new TestApp();var c=app.CreateClient();await app.With(async sp=>{await sp.GetRequiredService<RetroService>().SeedAsync();return true;});
  var links=await app.With(async sp=>(await sp.GetRequiredService<EngineDb>().ModelVersions.Include(x=>x.Components).SingleAsync(x=>x.ModelId==1&&x.Version==2)).Components.Select(x=>new PublishLink(x.ComponentId,x.Layer,x.Order)).ToList());
  Guid original=await app.With(async sp=>(await sp.GetRequiredService<EngineDb>().TransactionHeaders.FirstAsync(x=>x.PlacementId=="demo-b-1"&&x.PaymentMonth==new DateOnly(2026,11,1))).Id);
  (await c.PostAsJsonAsync($"/api/headers/{original}/payment-status",new ChangePaymentStatus("Paid","Pending"))).EnsureSuccessStatusCode();
  for(int version=3;version<=4;version++)
  {
   var draft=new PublishModel(1,version,"תיקון "+version,new(2026,11,1),new(2026,11,30),version==3?links.Where(x=>x.ComponentId!=10).ToList():links,[3404]);
   var sim=await c.PostAsJsonAsync("/api/model-publication/simulate",draft);sim.EnsureSuccessStatusCode();var token=(await sim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewToken").GetString()!;
   (await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
   if(version==3)
   {
    Guid latest=await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();var revision=await db.MonthlyRevisions.Where(x=>x.PlacementId=="demo-b-1"&&x.Month==new DateOnly(2026,11,1)).OrderByDescending(x=>x.CreatedAtUtc).FirstAsync();return(await db.TransactionHeaders.SingleAsync(x=>x.CalculationRunId==revision.RunId&&x.EntryType!="Reversal")).Id;});
    (await c.PostAsJsonAsync($"/api/headers/{latest}/payment-status",new ChangePaymentStatus("Paid","Pending"))).EnsureSuccessStatusCode();
   }
  }
  await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();var all=await db.TransactionHeaders.Where(x=>x.PlacementId=="demo-b-1"&&x.PaymentMonth==new DateOnly(2026,11,1)).ToListAsync();Assert.Equal(2,all.Count(x=>x.EntryType=="Reversal"));Assert.Equal(all.Single(x=>x.ModelVersion==4&&x.EntryType=="Calculation").Total,all.Where(x=>x.PaymentStatus!="Superseded").Sum(x=>x.Total));return true;});
 }
 [Fact] public async Task ForecastWritesMonthlyRevisionsAndResumeDoesNotDuplicate()
 {
  using var app=new TestApp();var c=app.CreateClient();var request=new ForecastRequest("resume","demo-b-1",new(2026,11,1),false,0,1);
  (await c.PostAsJsonAsync("/api/forecasts",request)).EnsureSuccessStatusCode();(await c.PostAsJsonAsync("/api/forecasts",request)).EnsureSuccessStatusCode();
  Assert.Equal(1,await app.With(sp=>sp.GetRequiredService<EngineDb>().MonthlyRevisions.CountAsync()));
  (await c.PostAsJsonAsync("/api/forecasts",request with{Offset=1})).EnsureSuccessStatusCode();Assert.Equal(2,await app.With(sp=>sp.GetRequiredService<EngineDb>().MonthlyRevisions.CountAsync()));
 }
}
