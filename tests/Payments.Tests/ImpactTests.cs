using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Xunit;

namespace Payments.Tests;
public class ImpactTests
{
    static ImpactRequest Request()=>new(1,2,new(2026,9,1),new(2026,9,30),[3404]);
    [Fact] public async Task SimulationComparesMonthlyPlacementsAndWritesNothing()
    {
        using var app=new TestApp();var client=app.CreateClient();
        var before=await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();return (await db.Consents.CountAsync(),await db.CalculationRuns.CountAsync(),await db.ModelVersions.CountAsync());});
        var response=await client.PostAsJsonAsync("/api/impact/simulate",Request());response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2,result.GetProperty("monthCount").GetInt32());Assert.False(result.GetProperty("partial").GetBoolean());
        Assert.Equal(2257m,result.GetProperty("net").GetDecimal()); // Two full months: doctor +925, profit +37, VAT +166.5 per placement.
        var after=await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();return (await db.Consents.CountAsync(),await db.CalculationRuns.CountAsync(),await db.ModelVersions.CountAsync());});Assert.Equal(before,after);
    }
    [Fact] public async Task ConsentIsIdempotentAndChangesOnlySelectedInstitution()
    {
        using var app=new TestApp();var client=app.CreateClient();var consent=new ConsentBatch(1,2,new(2026,9,1),[3404]);
        (await client.PostAsJsonAsync("/api/impact/consents",consent)).EnsureSuccessStatusCode();
        var again=await client.PostAsJsonAsync("/api/impact/consents",consent);again.EnsureSuccessStatusCode();
        Assert.Equal(0,(await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());
        var response=await client.PostAsJsonAsync("/api/impact/simulate",Request());response.EnsureSuccessStatusCode();
        Assert.Equal(0,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("net").GetDecimal());
        Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().Consents.CountAsync(x=>x.Institution==12380&&x.EffectiveFrom==new DateOnly(2026,9,1))));
    }
    [Fact] public async Task DraftSimulationDoesNotPublishAndBatchFailureRollsBackPublication()
    {
        using var app=new TestApp();var client=app.CreateClient();
        var links=await app.With(async sp=>(await sp.GetRequiredService<EngineDb>().ModelVersions.Include(x=>x.Components).SingleAsync(x=>x.ModelId==1&&x.Version==2)).Components.Select(x=>new PublishLink(x.ComponentId,x.Layer,x.Order)).ToList());
        var draft=new PublishModel(1,3,"טיוטה",new(2026,9,1),new(2027,12,31),links);
        var response=await client.PostAsJsonAsync("/api/impact/simulate",Request() with {TargetVersion=3,Draft=draft});response.EnsureSuccessStatusCode();
        Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().ModelVersions.AnyAsync(x=>x.Version==3)));
        var failure=await client.PostAsJsonAsync("/api/impact/publish",new {consent=new ConsentBatch(1,3,new(2026,9,1),[3404,9999]),draft});
        Assert.Equal(HttpStatusCode.UnprocessableEntity,failure.StatusCode);
        Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().ModelVersions.AnyAsync(x=>x.Version==3)));
        Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().Consents.AnyAsync(x=>x.ModelVersion==3)));
        (await client.PostAsJsonAsync("/api/impact/publish",new {consent=new ConsentBatch(1,3,new(2026,9,1),[3404]),draft})).EnsureSuccessStatusCode();
    }
    [Fact] public async Task LaterConsentsRemainEffectiveAndInvalidDatesAreRejected()
    {
        using var app=new TestApp();var client=app.CreateClient();
        var response=await client.PostAsJsonAsync("/api/impact/simulate",Request() with {To=new(2026,10,31)});response.EnsureSuccessStatusCode();
        var rows=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray();
        foreach(var row in rows.Where(x=>x.GetProperty("month").GetString()=="2026-10-01"))Assert.Equal(0,row.GetProperty("difference").GetDecimal());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await client.PostAsJsonAsync("/api/impact/simulate",Request() with {From=new(2026,9,2)})).StatusCode);
    }
}
