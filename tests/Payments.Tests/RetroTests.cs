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
public class RetroTests
{
    static async Task<PublishModel> Setup(TestApp app)
    {
        _=app.CreateClient();
        return await app.With(async sp=>
        {
            await sp.GetRequiredService<RetroService>().SeedAsync();
            var db=sp.GetRequiredService<EngineDb>();
            var model=await db.ModelVersions.Include(x=>x.Components).SingleAsync(x=>x.ModelId==1&&x.Version==2);
            return new PublishModel(1,3,"ללא רווח",new(2026,11,1),new(2026,12,31),model.Components.Where(x=>x.ComponentId!=10).Select(x=>new PublishLink(x.ComponentId,x.Layer,x.Order)).ToList());
        });
    }
    static async Task<string> Preview(HttpClient client,PublishModel draft)
    {
        var r=await client.PostAsJsonAsync("/api/model-publication/simulate",draft);r.EnsureSuccessStatusCode();
        var json=await r.Content.ReadFromJsonAsync<JsonElement>();return json.GetProperty("previewToken").GetString()!;
    }
    [Fact] public async Task SimulationReadsSavedTransactionsWithoutWritingAndIgnoresConsents()
    {
        using var app=new TestApp();var draft=await Setup(app);var client=app.CreateClient();
        int before=await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync());
        var r=await client.PostAsJsonAsync("/api/model-publication/simulate",draft);r.EnsureSuccessStatusCode();
        var json=await r.Content.ReadFromJsonAsync<JsonElement>();Assert.True(json.GetProperty("canExecute").GetBoolean());
        Assert.Equal(12,json.GetProperty("monthCount").GetInt32());Assert.True(json.GetProperty("decrease").GetDecimal()>0); Assert.True(json.GetProperty("increase").GetDecimal()>0); Assert.Equal(504.394m,json.GetProperty("net").GetDecimal());
        var partial=json.GetProperty("rows").EnumerateArray().Single(x=>x.GetProperty("placementId").GetString()=="demo-c-2"&&x.GetProperty("month").GetString()=="2026-11-01"); Assert.Equal(4053.354m,partial.GetProperty("before").GetDecimal()); Assert.Equal(3942.410m,partial.GetProperty("after").GetDecimal());
        Assert.All(json.GetProperty("rows").EnumerateArray(),x=>{Assert.True(x.GetProperty("wasStored").GetBoolean());Assert.Equal(3,x.GetProperty("afterVersion").GetInt32());});
        Assert.Equal(before,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
        Assert.False(await app.With(sp=>sp.GetRequiredService<EngineDb>().ModelVersions.AnyAsync(x=>x.Version==3)));
    }
    [Fact] public async Task ExecuteUpdatesCurrentResultsPreservesHistoryAndIsIdempotent()
    {
        using var app=new TestApp();var draft=await Setup(app);var client=app.CreateClient();var token=await Preview(client,draft);
        int before=await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync());
        var r=await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token));r.EnsureSuccessStatusCode();
        var retry=await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token));retry.EnsureSuccessStatusCode();
        Assert.Equal(before+12,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
        var current=await client.GetFromJsonAsync<JsonElement>("/api/model-publication/current");
        foreach(var row in current.EnumerateArray())
            if(string.CompareOrdinal(row.GetProperty("month").GetString(),"2026-11-01")>=0)
            {Assert.Equal(3,row.GetProperty("modelVersion").GetInt32());Assert.Equal(2,row.GetProperty("revisions").GetInt32());}
            else Assert.Equal(1,row.GetProperty("revisions").GetInt32());
        var run=await client.PostAsJsonAsync("/api/simulations",new CalculateRequest("test",1,3404,"demo-b-1",new(2026,11,15),Ownership.Private,"דירה",4));run.EnsureSuccessStatusCode();
        Assert.Equal(3,(await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("header").GetProperty("modelVersion").GetInt32());
        var stored=await app.With(sp=>sp.GetRequiredService<EngineDb>().MonthlyRevisions.Where(x=>x.PreviousId==null).Select(x=>x.RunId).FirstAsync());
        var replay=await client.PostAsJsonAsync($"/api/runs/{stored}/replay",new{});replay.EnsureSuccessStatusCode();
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matchesOriginal").GetBoolean());
    }
    [Fact] public async Task StaleSimulationAndChangedDraftAreRejected()
    {
        using var app=new TestApp();var draft=await Setup(app);var client=app.CreateClient();var token=await Preview(client,draft);
        var changed=await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft with {Name="שונה"},token));Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
        await app.With(async sp=>{await sp.GetRequiredService<PublicationService>().PublishPriceAsync(new("doctor",3,"רופא",ComponentKind.Staff,new(2026,11,1),new(2027,12,31),19000));return true;});
        var stale=await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token));Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
    }
    [Fact] public async Task FailedExecutionRollsBackModelTransactionsAndRevisionPointers()
    {
        using var app=new TestApp();var draft=await Setup(app);var client=app.CreateClient();var token=await Preview(client,draft);
        var before=await app.With(async sp=>{var d=sp.GetRequiredService<EngineDb>();return(await d.CalculationRuns.CountAsync(),await d.MonthlyRevisions.CountAsync());});
        await Assert.ThrowsAsync<BusinessException>(()=>app.With(sp=>sp.GetRequiredService<RetroService>().ExecuteAsync(new(draft,token),true)));
        var after=await app.With(async sp=>{var d=sp.GetRequiredService<EngineDb>();Assert.False(await d.ModelVersions.AnyAsync(x=>x.Version==3));Assert.Equal(0,await d.RetroBatches.CountAsync());return(await d.CalculationRuns.CountAsync(),await d.MonthlyRevisions.CountAsync());});Assert.Equal(before,after);
        (await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
    }
    [Fact] public async Task PartialMonthClipsToPlacementEndAndNextChangeUsesUpdatedBaseline()
    {
        using var app=new TestApp();var draft=await Setup(app);var client=app.CreateClient();var token=await Preview(client,draft);
        (await client.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
        var next=draft with {Version=4,From=new(2026,12,1)};
        var response=await client.PostAsJsonAsync("/api/model-publication/simulate",next);response.EnsureSuccessStatusCode();
        var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(0,json.GetProperty("net").GetDecimal());
        Assert.All(json.GetProperty("rows").EnumerateArray(),r=>Assert.Equal(3,r.GetProperty("beforeVersion").GetInt32()));
        var partial=draft with {Version=4,From=new(2027,3,1),To=new(2027,3,31)};
        (await client.PostAsJsonAsync("/api/model-publication/simulate",partial)).EnsureSuccessStatusCode();
    }
}
