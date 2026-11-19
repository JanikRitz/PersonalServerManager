using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LocalLibraryEndpointTests
{
    [Fact]
    public async Task RoutesRequireAuthenticationAndServeOnlySafePlainText()
    {
        var root = Directory.CreateTempSubdirectory("psm-local-http-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "fixture.md"), "<script>fixture</script>");
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Fixture", root, LocalLibraryKind.Notes);
            var store = new JsonLocalLibraryStore(Path.Combine(root, "instances.json"));
            await store.SaveAsync(instance);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<ILocalLibraryStore>(store);
            builder.Services.AddSingleton<LocalLibraryClient>();
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            builder.Services.AddAuthorization();
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapLocalLibraryEndpoints();
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var item = Assert.Single(await new LocalLibraryClient().SearchAsync(instance, "fixture", 25));
            using var unauthorized = await http.GetAsync(item.ItemUrl);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var unauthorizedCover = await http.GetAsync($"/libraries/{instance.Id:D}/covers/1");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedCover.StatusCode);
            http.DefaultRequestHeaders.Add("X-Fixture-Authorization", "yes");
            using var response = await http.GetAsync(item.ItemUrl);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("<script>fixture</script>", await response.Content.ReadAsStringAsync());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
            using var missing = await http.GetAsync($"/libraries/{Guid.NewGuid():D}/items/{item.Id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var badId = await http.GetAsync($"/libraries/{instance.Id:D}/items/fixture.md");
            Assert.Equal(HttpStatusCode.NotFound, badId.StatusCode);
            using var invalidCover = await http.GetAsync($"/libraries/{instance.Id:D}/covers/1");
            Assert.Equal(HttpStatusCode.NotFound, invalidCover.StatusCode);
            var bookDirectory = Directory.CreateDirectory(Path.Combine(root, "book")).FullName;
            await File.WriteAllTextAsync(Path.Combine(bookDirectory, "fixture.pdf"), "pdf fixture");
            await File.WriteAllBytesAsync(Path.Combine(bookDirectory, "cover.jpg"), [255, 216, 255, 217]);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "metadata.db"), Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT, path TEXT);
                    CREATE TABLE data (book INTEGER, name TEXT, format TEXT);
                    INSERT INTO books VALUES (1, 'Fixture', 'book');
                    INSERT INTO data VALUES (1, 'fixture', 'PDF');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var calibre = instance with { Id = Guid.NewGuid(), Kind = LocalLibraryKind.CalibreText };
            await store.SaveAsync(calibre);
            using var download = await http.GetAsync($"/libraries/{calibre.Id:D}/items/1");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("book-1.pdf", download.Content.Headers.ContentDisposition.FileNameStar);
            Assert.Equal("pdf fixture", await download.Content.ReadAsStringAsync());
            using var cover = await http.GetAsync($"/libraries/{calibre.Id:D}/covers/1");
            Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
            Assert.Equal("image/jpeg", cover.Content.Headers.ContentType!.MediaType);
            await app.StopAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers["X-Fixture-Authorization"] == "yes"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "fixture")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}