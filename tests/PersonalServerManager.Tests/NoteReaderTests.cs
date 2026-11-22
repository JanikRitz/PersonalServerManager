using System.Security.Cryptography;
using System.Text;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class NoteReaderTests
{
    private static string Id(string relativePath) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));

    [Fact]
    public async Task RendersFoamNoteWithPanelLinksImagesAndMediaReferences()
    {
        var root = Directory.CreateTempSubdirectory("psm-note-reader-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(Path.Combine(root, "img"));
            await File.WriteAllTextAsync(Path.Combine(root, "index.md"), """
                ---
                title: Fixture <Title>
                tags: [alpha, beta]
                author:
                  name: Jane
                ---
                # Intro

                ## Details

                See [[Other]], [[sub/Dup|aliased]], [[Dup]], [[Missing]] and [[Other#Part Two]].

                ![pic](img/pic.png) ![[pic.png]] [outside](../secret.png) [bad](javascript:alert(1)) [rel](Other.md#part-two)

                @hydrusPreview{42} @hydrus{43} @stashImage{7|Caption|size:original} @stashVideo{8} @stashPerformer{9} @stashMarker{10:11}

                `@stashImage{1}` and `[[Other]]`

                ![[Other#Part Two]]
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "Other.md"), "# Other\n\nIntro text\n\n## Part Two\n\nSecond part [[index]]\n\n## Part Three\n\nNot embedded");
            await File.WriteAllTextAsync(Path.Combine(root, "Dup.md"), "root dup");
            await File.WriteAllTextAsync(Path.Combine(root, "sub", "Dup.md"), "sub dup [[Dup]]\n\n![[self]]");
            await File.WriteAllTextAsync(Path.Combine(root, "sub", "self.md"), "![[self]]");
            await File.WriteAllBytesAsync(Path.Combine(root, "img", "pic.png"), [0x89, 0x50, 0x4e, 0x47]);
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Vault", root, LocalLibraryKind.Notes);
            var hydrus = new HydrusInstance(Guid.NewGuid(), "Hydrus", new Uri("http://127.0.0.1:45869/"), null);
            var stash = new StashInstance(Guid.NewGuid(), "Stash", new Uri("http://127.0.0.1:9999/graphql"), new Uri("http://127.0.0.1:9999/"), null);

            var html = await NoteReader.RenderPageAsync(instance, Id("index.md"), hydrus, stash);

            Assert.NotNull(html);
            Assert.Contains("<title>Fixture &lt;Title&gt;</title>", html);
            Assert.DoesNotContain("tags: [alpha", html);
            Assert.Contains("<ul class=\"chips\"><li>alpha</li><li>beta</li></ul>", html);
            Assert.Contains("<dt>name</dt><dd>Jane</dd>", html);
            Assert.Contains("<a href=\"#intro\">Intro</a>", html);
            Assert.Contains("class=\"toc-level-1\"><a href=\"#details\">Details</a>", html);
            var prefix = $"/libraries/{instance.Id:D}";
            Assert.Contains($"<a class=\"wikilink\" href=\"{prefix}/read/{Id("Other.md")}\">Other</a>", html);
            Assert.Contains($"<a class=\"wikilink\" href=\"{prefix}/read/{Id("sub\\Dup.md")}\">aliased</a>", html);
            Assert.Contains($"<a class=\"wikilink\" href=\"{prefix}/read/{Id("Dup.md")}\">Dup</a>", html);
            Assert.Contains($"href=\"{prefix}/read/{Id("Other.md")}#part-two\">Other › Part Two</a>", html);
            Assert.Contains("<span class=\"wikilink missing\"", html);
            Assert.Contains($"<img src=\"{prefix}/files/img/pic.png\" alt=\"pic\"", html);
            Assert.Contains($"<img src=\"{prefix}/files/img/pic.png\" alt=\"pic.png\" loading=\"lazy\">", html);
            Assert.Contains("href=\"../secret.png\"", html);
            Assert.DoesNotContain("javascript:", html);
            Assert.Contains($"href=\"{prefix}/read/{Id("Other.md")}#part-two\">rel</a>", html);
            Assert.Contains($"href=\"/media/hydrus/{hydrus.Id:D}/42\"", html);
            Assert.Contains($"src=\"/previews/hydrus/{hydrus.Id:D}/files/42\"", html);
            Assert.Contains($"src=\"/media/hydrus/{hydrus.Id:D}/43\"", html);
            Assert.Contains($"src=\"/previews/stash/{stash.Id:D}/images/7\" alt=\"Caption\"", html);
            Assert.Contains("<span class=\"media-caption\">Caption</span>", html);
            Assert.Contains("href=\"http://127.0.0.1:9999/images/7\"", html);
            Assert.Contains($"src=\"/previews/stash/{stash.Id:D}/scenes/8\"", html);
            Assert.Contains($"src=\"/previews/stash/{stash.Id:D}/performers/9\"", html);
            Assert.Contains($"src=\"/previews/stash/{stash.Id:D}/markers/11\"", html);
            Assert.Contains("href=\"http://127.0.0.1:9999/scenes/10\"", html);
            Assert.Contains("<code>@stashImage{1}</code>", html);
            Assert.Contains("<code>[[Other]]</code>", html);
            Assert.Contains("<section class=\"embed\">", html);
            Assert.Contains("Second part", html);
            Assert.DoesNotContain("Not embedded", html);
            Assert.DoesNotContain("<p><section", html);
            Assert.Contains($"href=\"{prefix}/read/{Id("Other.md")}\" title=\"Other.md\">Other</a>", html);
            Assert.DoesNotContain($"title=\"Dup.md\"", html);

            var sub = await NoteReader.RenderPageAsync(instance, Id("sub\\Dup.md"), null, null);
            Assert.Contains($"href=\"{prefix}/read/{Id("sub\\Dup.md")}\">Dup</a>", sub);
            Assert.Contains("Embedding skipped: the note embeds itself.", sub);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MediaReferencesWithoutLinkedInstancesStayVisibleAsText()
    {
        var root = Directory.CreateTempSubdirectory("psm-note-reader-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "note.md"), "@hydrusPreview{1} @stashMarker{2:3} @stashImage{x} ---\nnot: front matter");
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Vault", root, LocalLibraryKind.Notes);

            var html = await NoteReader.RenderPageAsync(instance, Id("note.md"), null, null);

            Assert.Contains("title=\"No Hydrus instance is linked to this notes library\">@hydrusPreview{1}</span>", html);
            Assert.Contains("title=\"No Stash instance is linked to this notes library\">@stashMarker{2:3}</span>", html);
            Assert.Contains("@stashImage{x}", html);
            Assert.DoesNotContain("<summary>Properties</summary>", html);
            Assert.Null(await NoteReader.RenderPageAsync(instance, Id("missing.md"), null, null));
            Assert.Null(await NoteReader.RenderPageAsync(instance with { Kind = LocalLibraryKind.Calibre }, Id("note.md"), null, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RendersSanitizedEmbeddedHtmlWithResolvedReferences()
    {
        var root = Directory.CreateTempSubdirectory("psm-note-reader-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "image.png"), [0x89, 0x50]);
            await File.WriteAllTextAsync(Path.Combine(root, "Other.md"), "# Other");
            await File.WriteAllTextAsync(Path.Combine(root, "note.md"), """
                <details><summary>Area</summary>

                Some more text
                and image
                ![image](./image.png)
                </details>

                <div class="box" onclick="alert(1)">Raw [[Other]] @hydrusPreview{5} <img src="image.png" onerror="alert(2)"></div>

                <script>alert(3)</script><a href="javascript:alert(4)">bad</a>
                """);
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Vault", root, LocalLibraryKind.Notes);
            var hydrus = new HydrusInstance(Guid.NewGuid(), "Hydrus", new Uri("http://127.0.0.1:45869/"), null);

            var html = await NoteReader.RenderPageAsync(instance, Id("note.md"), hydrus, null);

            var prefix = $"/libraries/{instance.Id:D}";
            Assert.Contains("<details><summary>Area</summary>", html);
            Assert.Contains($"<img src=\"{prefix}/files/image.png\" alt=\"image\">", html);
            Assert.Contains("</details>", html);
            Assert.Contains($"<a class=\"wikilink\" href=\"{prefix}/read/{Id("Other.md")}\">Other</a>", html);
            Assert.Contains($"src=\"/previews/hydrus/{hydrus.Id:D}/files/5\"", html);
            Assert.Contains($"<img src=\"{prefix}/files/image.png\">", html);
            Assert.Contains("<div class=\"box\">", html);
            Assert.DoesNotContain("alert(", html);
            Assert.DoesNotContain("<script", html);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ServesOnlyImagesInsideTheNotesRoot()
    {
        var root = Directory.CreateTempSubdirectory("psm-note-reader-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vault", ".git"));
            await File.WriteAllBytesAsync(Path.Combine(root, "vault", "pic one.png"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "vault", ".git", "pic.png"), [1]);
            await File.WriteAllTextAsync(Path.Combine(root, "vault", "evil.svg"), "<svg/>");
            await File.WriteAllBytesAsync(Path.Combine(root, "outside.png"), [1]);
            var instance = new LocalLibraryInstance(Guid.NewGuid(), "Vault", Path.Combine(root, "vault"), LocalLibraryKind.Notes);
            var client = new LocalLibraryClient();

            await using (var image = await client.OpenNoteImageAsync(instance, "pic one.png"))
            {
                Assert.NotNull(image);
                Assert.Equal("image/png", image.ContentType);
            }
            Assert.Null(await client.OpenNoteImageAsync(instance, "evil.svg"));
            Assert.Null(await client.OpenNoteImageAsync(instance, ".git/pic.png"));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.OpenNoteImageAsync(instance, "../outside.png"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
