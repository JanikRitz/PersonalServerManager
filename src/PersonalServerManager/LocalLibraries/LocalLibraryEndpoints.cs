using Microsoft.Data.Sqlite;

namespace PersonalServerManager.LocalLibraries;

public static class LocalLibraryEndpoints
{
    public static void MapLocalLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/libraries/{instanceId:guid}/items/{itemId}", (
            Guid instanceId, string itemId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, itemId, false, store, client, context)).RequireAuthorization();
        app.MapGet("/libraries/{instanceId:guid}/covers/{bookId}", (
            Guid instanceId, string bookId, ILocalLibraryStore store, LocalLibraryClient client, HttpContext context) =>
                ServeAsync(instanceId, bookId, true, store, client, context)).RequireAuthorization();
    }

    private static async Task<IResult> ServeAsync(Guid instanceId, string itemId, bool cover,
        ILocalLibraryStore store, LocalLibraryClient client, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        try
        {
            var instance = (await store.GetAllAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null) return Results.NotFound();
            var file = cover
                ? await client.OpenCoverAsync(instance, itemId, context.RequestAborted)
                : await client.OpenItemAsync(instance, itemId, context.RequestAborted);
            return file is null ? Results.NotFound() : Results.File(file.Content, file.ContentType, file.FileName);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or SqliteException)
        {
            return Results.NotFound();
        }
    }
}