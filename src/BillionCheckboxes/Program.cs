using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;
using BillionCheckboxes;
using BillionCheckboxes.Models;
using BillionCheckboxes.Views;
using LiteDB;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using StarFederation.Datastar.DependencyInjection;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Serializers;
using Index = BillionCheckboxes.Views.Index;
using JsonSerializer = System.Text.Json.JsonSerializer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDatastar();

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var htmlRenderer = new HtmlRenderer(app.Services, loggerFactory);

app.UseHttpsRedirection();

app.MapStaticAssets();

//Setup litedb
/*var litedb = new LiteDatabase("Filename=checkboxes.db");
var collection = litedb.GetCollection<CheckboxModel>("checkboxes");*/

//Setup zonetree
var factory = new ZoneTreeFactory<int, bool>();

factory.ConfigureDiskSegmentOptions(opt =>
{
    opt.DiskSegmentMode = DiskSegmentMode.MultiPartDiskSegment;

    opt.CompressionMethod = CompressionMethod.LZ4;
    opt.CompressionBlockSize = 1024 * 1024 * 10;
});

factory.ConfigureWriteAheadLogOptions(opt =>
{
    opt.CompressionMethod = CompressionMethod.LZ4;
    opt.CompressionBlockSize = 32768;
    opt.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed;
});

factory.SetComparer(new Int32ComparerAscending());

factory.SetKeySerializer(new Int32Serializer());
factory.SetValueSerializer(new BooleanSerializer());

factory.SetDataDirectory(app.Environment.IsDevelopment() ? "./db" : "/app/db");

var zoneTree = factory.OpenOrCreate();

_ = zoneTree.CreateMaintainer();

/*//Test performance
var timestamp = Stopwatch.GetTimestamp();
// 1 billion
var count = 1_000_000_000;
for (int i = 1; i <= count; i++)
{
    zoneTree.Upsert(i, i % 2 == 0);
}
var elapsed = Stopwatch.GetElapsedTime(timestamp);
Console.WriteLine($"Elapsed time: {elapsed.TotalSeconds} seconds");*/

app.MapGet("/", async (HttpContext context) =>
{
    var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var output = await htmlRenderer.RenderComponentAsync<Index>(ParameterView.Empty);

        return output.ToHtmlString();
    });

    //Set the content type to text/html
    context.Response.ContentType = "text/html";

    return html;
});

var notifyChannel = new ConcurrentDictionary<Guid, IDatastarServerSentEventService>();
app.MapGet("/checkbox", async (HttpContext context, IDatastarServerSentEventService sse,
    CancellationToken cancellationToken) =>
{
    var checkBoxModel = new CheckboxesModel
    {
        StartId = 1,
        Amount = 10,
    };

    using var iterator = zoneTree.CreateIterator();
    iterator.Seek(1);
    while (iterator.Next())
    {
        var key = iterator.Current.Key;
        var value = iterator.Current.Value;

        if (value)
        {
            checkBoxModel.CheckedIds.Add(key);
        }

        if (key >= checkBoxModel.TotalCheckboxes)
        {
            break;
        }
    }

    //Send the initial state
    var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
    {
        var parameters = new Dictionary<string, object>
        {
            { nameof(CheckboxesModel), checkBoxModel }
        };
        
        var output = await htmlRenderer.RenderComponentAsync<Checkboxes>(ParameterView.FromDictionary(parameters!));

        return output.ToHtmlString();
    });
    
    await sse.MergeFragmentsAsync(html);
    var initialSignal = checkBoxModel.ToSignal();
    await Task.Delay(100, cancellationToken);
    await sse.MergeSignalsAsync(initialSignal);
    
    var guid = Guid.NewGuid();
    try
    {
        notifyChannel.TryAdd(guid, sse);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(-1, cancellationToken);
        }
    }
    catch (Exception e)
    {
        //Ignore
    }
    finally
    {
        notifyChannel.TryRemove(guid, out _);
    }
});

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    PropertyNameCaseInsensitive = false
};
app.MapPost("/checkbox/{id:int}", async (int id, HttpContext httpContext) =>
{
    var streamReader = new StreamReader(httpContext.Request.Body);
    var body = await streamReader.ReadToEndAsync();
    body = body.Replace("\"\"", "false");
    
    //Why??
    var bodyJson = body.Replace("true", "\"true\"").Replace("false", "\"false\"");

    var boxes = JsonSerializer.Deserialize<CheckBoxSignal>(bodyJson, jsonOptions);

    if (boxes == null)
    {
        return Results.BadRequest("Invalid request");
    }

    var value = bool.Parse(boxes.Boxes[id - 1]);

    zoneTree.Upsert(id, value);

    var checkBoxModel = new CheckBoxModel
    {
        Id = id,
        Value = value,
    };

    //await notifyChannel.Writer.WriteAsync(body);

    foreach (var sse in notifyChannel.Values)
    {
        await sse.MergeSignalsAsync(body);
    }

    return Results.Ok();
});

app.Run();