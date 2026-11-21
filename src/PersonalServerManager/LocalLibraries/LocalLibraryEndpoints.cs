using Markdig;
using Microsoft.Data.Sqlite;

namespace PersonalServerManager.LocalLibraries;

public static class LocalLibraryEndpoints
{
    private static readonly MarkdownPipeline NotePipeline = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().Build();

    public static void MapLocalLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/libraries/{instanceId:guid}/read/{itemId}", (
            Guid instanceId, string itemId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, itemId, false, store, client, context, readNote: true)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/items/{itemId}", (
            Guid instanceId, string itemId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, itemId, false, store, client, context)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/covers/{bookId}", (
            Guid instanceId, string bookId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, bookId, true, store, client, context)).RequireAuthorization();
    }

    private static async Task<IResult> ServeAsync(Guid instanceId, string itemId, bool cover,
        ILocalLibraryStore store, LocalLibraryClient client, HttpContext context, bool readNote = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        try
        {
            var instance = (await store.GetAllAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null || (readNote && instance.Kind != LocalLibraryKind.Notes)) return Results.NotFound();
            var file = cover
                ? await client.OpenCoverAsync(instance, itemId, context.RequestAborted)
                : await client.OpenItemAsync(instance, itemId, context.RequestAborted);
            if (file is null) return Results.NotFound();
            if (!readNote) return Results.File(file.Content, file.ContentType, file.FileName);

            await using var note = file;
            using var reader = new StreamReader(note.Content, leaveOpen: true);
            var body = Markdown.ToHtml(await reader.ReadToEndAsync(context.RequestAborted), NotePipeline);
            var downloadUrl = $"/libraries/{instanceId:D}/items/{Uri.EscapeDataString(itemId)}";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.Content($$"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="referrer" content="no-referrer"><title>Note reader</title>
                <style>
                :root { color-scheme: light dark; }
                body { max-width: 46rem; margin: auto; padding: 1.5rem; font: 1rem/1.6 Georgia, serif; overflow-wrap: anywhere; }
                nav { font: .9rem/1.5 sans-serif; border-bottom: 1px solid GrayText; padding-bottom: .75rem; }
                pre { overflow: auto; padding: .8rem; background: color-mix(in srgb, CanvasText 8%, Canvas); }
                table { border-collapse: collapse; } td, th { border: 1px solid GrayText; padding: .3rem .6rem; }
                </style></head>
                <body><nav><a href="{{downloadUrl}}">Original note</a></nav><main>{{body}}</main></body></html>
                """, "text/html; charset=utf-8");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(502);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(504);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or SqliteException or System.Text.Json.JsonException)
        {
            return Results.NotFound();
        }
    }
}