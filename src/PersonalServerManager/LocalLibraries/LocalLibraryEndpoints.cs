using Microsoft.Data.Sqlite;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Stash;

namespace PersonalServerManager.LocalLibraries;

public static class LocalLibraryEndpoints
{
    public static void MapLocalLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/libraries/{instanceId:guid}/read/{itemId}", (
            Guid instanceId, string itemId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, itemId, false, store, client, context, readNote: true)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/files/{**path}", (
            Guid instanceId, string path, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, path, false, store, client, context, noteImage: true)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/items/{itemId}", (
            Guid instanceId, string itemId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, itemId, false, store, client, context)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/covers/{bookId}", (
            Guid instanceId, string bookId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, bookId, true, store, client, context)).RequireAuthorization();
    }

    private static async Task<IResult> ServeAsync(Guid instanceId, string itemId, bool cover,
        ILocalLibraryStore store, LocalLibraryClient client, HttpContext context, bool readNote = false, bool noteImage = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        try
        {
            var instance = (await store.GetAllAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null || ((readNote || noteImage) && instance.Kind != LocalLibraryKind.Notes)) return Results.NotFound();
            if (noteImage)
            {
                var image = await client.OpenNoteImageAsync(instance, itemId, context.RequestAborted);
                return image is null ? Results.NotFound() : Results.File(image.Content, image.ContentType);
            }
            if (readNote)
            {
                var hydrus = instance.HydrusInstanceId is { } hydrusId && context.RequestServices.GetService<IHydrusInstanceStore>() is { } hydrusStore
                    ? (await hydrusStore.GetAllAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == hydrusId) : null;
                var stash = instance.StashInstanceId is { } stashId && context.RequestServices.GetService<IStashInstanceStore>() is { } stashStore
                    ? (await stashStore.GetAllAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == stashId) : null;
                var page = await NoteReader.RenderPageAsync(instance, itemId, hydrus, stash, context.RequestAborted);
                if (page is null) return Results.NotFound();
                // No sandbox: an opaque origin would drop the SameSite=Strict auth cookie for images and links.
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                return Results.Content(page, "text/html; charset=utf-8");
            }
            var file = cover
                ? await client.OpenCoverAsync(instance, itemId, context.RequestAborted)
                : await client.OpenItemAsync(instance, itemId, context.RequestAborted);
            if (file is null) return Results.NotFound();
            return Results.File(file.Content, file.ContentType, file.FileName);
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