using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;
using Xunit;
namespace Payments.Tests;
public class PlacementWorkflowTests
{
 static CreatePlacementRequest Request(string id="new-period",string key="create-period")=>new(key,new Placement{Id=id,Institution=3404,ModelId=1,From=new(2026,9,15),To=new(2027,6,20),SupportLevel=4});
 [Fact] public async Task CreatesMonthsAtomicallyWithConsentedVersionsAndIdempotentRetry()
 {
  using var app=new TestApp();var c=app.CreateClient();var r=Request();
  var response=await c.PostAsJsonAsync("/api/placements/create-and-calculate",r);response.EnsureSuccessStatusCode();
  var data=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("2026-12-31",data.GetProperty("calculationEnd").GetString());Assert.Equal(4,data.GetProperty("months").GetArrayLength());
  var months=data.GetProperty("months").EnumerateArray().ToArray();Assert.Equal(1,months[0].GetProperty("modelVersion").GetInt32());Assert.Equal(2,months[1].GetProperty("modelVersion").GetInt32());
  (await c.PostAsJsonAsync("/api/placements/create-and-calculate",r)).EnsureSuccessStatusCode();
  Assert.Equal(4,await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionHeaders.CountAsync(x=>x.PlacementId==r.Placement.Id)));
  var search=await c.GetFromJsonAsync<JsonElement>("/api/placements/search?q=create-period");Assert.Equal(1,search.GetProperty("total").GetInt32());
  var detail=await c.GetFromJsonAsync<JsonElement>($"/api/placements/{r.Placement.Id}/transactions");Assert.Equal(4,detail.GetProperty("transactions").EnumerateArray().Count(x=>x.GetProperty("isCurrent").GetBoolean()));
  r.Placement.SupportLevel=5;Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/placements/create-and-calculate",r)).StatusCode);
 }
 [Fact] public async Task SameMonthPartialPeriodCreatesOneProratedMonth()
 {
  using var app=new TestApp();var c=app.CreateClient();var r=Request();r.Placement.From=new(2026,11,4);r.Placement.To=new(2026,11,6);
  (await c.PostAsJsonAsync("/api/placements/create-and-calculate",r)).EnsureSuccessStatusCode();
  await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();var run=await db.CalculationRuns.SingleAsync(x=>x.RequestKey=="create-period:202611");var input=JsonDefaults.Read<CalculateRequest>(run.RequestJson);Assert.Equal(.1m,input.MonthlyFactor);Assert.Equal(new DateOnly(2026,11,4),input.EventDate);return true;});
 }
 [Fact] public async Task FailingLaterMonthDoesNotCreatePlacementOrEarlierTransactions()
 {
  using var app=new TestApp();var c=app.CreateClient();var r=Request();r.Placement.From=new(2027,12,1);r.Placement.To=new(2028,3,1);
  await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();var institution=await db.Institutions.SingleAsync(x=>x.Id==3404);institution.BusinessYearEndMonth=6;institution.BusinessYearEndDay=30;await db.SaveChangesAsync();return true;});
  Assert.Equal(HttpStatusCode.UnprocessableEntity,(await c.PostAsJsonAsync("/api/placements/create-and-calculate",r)).StatusCode);
  Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().Placements.AnyAsync(x=>x.Id==r.Placement.Id)));
  Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.AnyAsync(x=>x.RequestKey.StartsWith("create-period:"))));
 }
 [Fact] public async Task PlacementHistoryShowsRetroChangesOnlyForSelectedInstitution()
 {
  using var app=new TestApp();var c=app.CreateClient();var r=Request();r.Placement.From=new(2026,11,1);r.Placement.To=new(2026,11,30);
  (await c.PostAsJsonAsync("/api/placements/create-and-calculate",r)).EnsureSuccessStatusCode();
  var links=await app.With(async sp=>(await sp.GetRequiredService<EngineDb>().ModelVersions.Include(x=>x.Components).SingleAsync(x=>x.ModelId==1&&x.Version==2)).Components.Where(x=>x.ComponentId!=10).Select(x=>new PublishLink(x.ComponentId,x.Layer,x.Order)).ToList());
  var draft=new PublishModel(1,3,"שינוי לבדיקת תיק השמה",new(2026,11,1),new(2026,11,30),links,[3404]);
  var sim=await c.PostAsJsonAsync("/api/model-publication/simulate",draft);sim.EnsureSuccessStatusCode();var token=(await sim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewToken").GetString()!;
  (await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
  var detail=await c.GetFromJsonAsync<JsonElement>($"/api/placements/{r.Placement.Id}/transactions");var rows=detail.GetProperty("transactions").EnumerateArray().ToArray();Assert.Equal(2,rows.Length);
  var current=Assert.Single(rows,x=>x.GetProperty("isCurrent").GetBoolean());Assert.Equal(3,current.GetProperty("header").GetProperty("modelVersion").GetInt32());Assert.NotEqual(JsonValueKind.Null,current.GetProperty("batchToken").ValueKind);
  Assert.Contains(rows,x=>x.GetProperty("header").GetProperty("paymentStatus").GetString()=="Superseded");
 }
}
