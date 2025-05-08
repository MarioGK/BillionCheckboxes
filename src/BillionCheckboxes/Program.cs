using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BillionCheckboxes.Models;
using BillionCheckboxes.Views;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc;
using StarFederation.Datastar;
using StarFederation.Datastar.DependencyInjection;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Serializers;
using Utf8StringInterpolation;
using ZLogger;
using Index = BillionCheckboxes.Views.Index;
using JsonSerializer = System.Text.Json.JsonSerializer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();

    loggingBuilder.AddZLoggerConsole(options =>
    {
        options.FullMode = BackgroundBufferFullMode.Grow;
        options.ConfigureEnableAnsiEscapeCode = true;

        options.UsePlainTextFormatter(formatter =>
        {
            formatter.SetPrefixFormatter($"\e[33m{0}\e[0m [{1}] (\e[34m{2}\e[0m) ",
                (in MessageTemplate template, in LogInfo info) =>
                {
                    var shortLogLevelString = info.LogLevel switch
                    {
                        LogLevel.Trace => "\e[37mTRC\e[0m", // White
                        LogLevel.Debug => "\e[35mDBG\e[0m", // Magenta
                        LogLevel.Information => "\e[32mINF\e[0m", // Green
                        LogLevel.Warning => "\e[33mWRN\e[0m", // Yellow
                        LogLevel.Error => "\e[31mERR\e[0m", // Red
                        LogLevel.Critical => "\e[208mCRT\e[0m", // Orange
                        _ => "\e[37m???\e[0m" // White
                    };

                    try
                    {
                        template.Format(info.Timestamp, shortLogLevelString, info.Category);
                    }
                    catch (Exception e)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(e);
                        Console.ResetColor();
                    }
                });

            formatter.SetExceptionFormatter((writer, ex) =>
                Utf8String.Format(writer, $"\n\e[31m{ex.Message}\e[0m"));
        });
    });
});

builder.Services.AddDatastar();

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var htmlRenderer = new HtmlRenderer(app.Services, loggerFactory);

app.MapStaticAssets();

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

/*//Test performance of KV, 120s to insert 1 billion items my PC
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
app.MapGet("/checkbox/sse", async (HttpContext context, IDatastarServerSentEventService sse,
    CancellationToken cancellationToken, [FromServices] ILogger<Program> logger) =>
{
    try
    {
        var firstTimeStamp = Stopwatch.GetTimestamp();
        var checkBoxModel = new CheckboxesModel
        {
            Offset = 1,
            Limit = 5000,
        };

        var keyValueTimeStamp = Stopwatch.GetTimestamp();
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

        var keyValueElapsed = Stopwatch.GetElapsedTime(keyValueTimeStamp);

        var htmlTimeStamp = Stopwatch.GetTimestamp();
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
        var htmlElapsed = Stopwatch.GetElapsedTime(htmlTimeStamp);

        var mergeTimeStamp = Stopwatch.GetTimestamp();
        await sse.MergeFragmentsAsync(html);
        var initialSignal = checkBoxModel.ToSignal();
        //await Task.Delay(100, cancellationToken);
        await sse.MergeSignalsAsync(initialSignal);
        var mergeElapsed = Stopwatch.GetElapsedTime(mergeTimeStamp);

        var firstElapsed = Stopwatch.GetElapsedTime(firstTimeStamp);
        logger.ZLogInformation(
            $"First time: {firstElapsed.TotalMilliseconds}ms, KeyValue time: {keyValueElapsed.TotalMilliseconds}ms, HTML time: {htmlElapsed.TotalMilliseconds}ms, Merge time: {mergeElapsed.TotalMilliseconds}ms");

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
    }
    catch (Exception e)
    {
        logger.ZLogError(e, $"Error in SSE");
    }
});

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    PropertyNameCaseInsensitive = false
};

app.MapPost("/checkbox/update/{id:int}",
    async (int id, HttpContext httpContext, [FromServices] ILogger<Program> logger) =>
    {
        try
        {
            logger.ZLogInformation($"Received checkbox {id}");
            var streamReader = new StreamReader(httpContext.Request.Body);
            var body = await streamReader.ReadToEndAsync();
            //body = body.Replace("\"\"", "false");

            //TODO: Remove this hack when datastar adds json support
            var bodyJson = body.Replace("true", "\"true\"").Replace("false", "\"false\"");

            var boxes = JsonSerializer.Deserialize<CheckBoxSignal>(bodyJson, jsonOptions);

            if (boxes == null)
            {
                return Results.BadRequest("Invalid request");
            }

            //Kinda hacky, but it works
            var value = bool.Parse(boxes.Boxes[id - 1]);

            zoneTree.Upsert(id, value);

            logger.ZLogInformation($"Sending updates to {notifyChannel.Count} clients");
            foreach (var sse in notifyChannel.Values)
            {
                await sse.MergeSignalsAsync(body);
            }

            return Results.Ok();
        }
        catch (Exception e)
        {
            logger.ZLogError(e, $"Error in checkbox update");
            return Results.Problem("Error in checkbox update");
        }
    });

// Pagination
app.MapGet("/checkbox/pagination", async (HttpContext context, IDatastarSignalsReaderService reader,
    IDatastarServerSentEventService sse, [FromServices] ILogger<Program> logger) =>
{
    try
    {
        var streamReader = new StreamReader(context.Request.Body);
        var body = await streamReader.ReadToEndAsync();

        var signalRaw = await reader.ReadSignalsAsync();
        var signal = await reader.ReadSignalsAsync<CheckBoxPaginationSignal>();

        if (signal == null)
        {
            logger.ZLogWarning($"Signal is null");
            signal = new CheckBoxPaginationSignal
            {
                Limit = 1000,
                Offset = 1
            };
        }

        //var firstTimeStamp = Stopwatch.GetTimestamp();
        var checkBoxModel = new CheckboxesModel
        {
            Offset = signal.Offset + signal.Limit, //Get the next page offset
            Limit = signal.Limit,
        };
        
        if (checkBoxModel.Offset < 1)
        {
            logger.ZLogWarning($"Offset is less than 1");
            return;
        }

        //var keyValueTimeStamp = Stopwatch.GetTimestamp();
        using var iterator = zoneTree.CreateIterator();
        iterator.Seek(signal.Offset);
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
        
        //var keyValueElapsed = Stopwatch.GetElapsedTime(keyValueTimeStamp);

        //var htmlTimeStamp = Stopwatch.GetTimestamp();
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

        await sse.MergeFragmentsAsync(html, new ServerSentEventMergeFragmentsOptions
        {
            MergeMode = FragmentMergeMode.Outer,
            Selector = "#loading_message"
        });

        await Task.Delay(100);
        var signals = checkBoxModel.ToSignal();
        await sse.MergeSignalsAsync(signals);
        
        logger.ZLogInformation($"Sending pagination");
    }
    catch (Exception e)
    {
        logger.ZLogError(e, $"Error in pagination");
    }
});

app.Run();