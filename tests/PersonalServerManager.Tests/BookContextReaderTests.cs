using System.IO.Compression;
using System.Text;
using PersonalServerManager.Covers;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class BookContextReaderTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 1, 2, 3];

    [Fact]
    public async Task EpubFollowsSpineOrderAndStripsMarkup()
    {
        var epub = Zip(
            ("META-INF/container.xml", """<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles></container>"""),
            ("OEBPS/content.opf", """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf"><manifest><item id="two" href="text/two.xhtml"/><item id="one" href="text/one%20a.xhtml"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>"""),
            ("OEBPS/text/one a.xhtml", "<html><head><title>Ignored title</title></head><body><script>ignored()</script><h1>Chapter &amp; One</h1><p>First   line.</p></body></html>"),
            ("OEBPS/text/two.xhtml", "<html><body><p>Second line.</p><p>Third line.</p></body></html>"));

        Assert.Equal("Chapter & One\nFirst line.\nSecond line.", await BookContextReader.ReadLinesAsync(new MemoryStream(epub), "EPUB", 3));
    }

    [Fact]
    public async Task EpubWithoutSpineFallsBackToHtmlDocuments()
    {
        var epub = Zip(("b.html", "<p>Later</p>"), ("a.xhtml", "<p>Earlier</p>"));
        Assert.Equal("Earlier\nLater", await BookContextReader.ReadLinesAsync(new NonSeekableStream(epub), "epub", 10));
    }

    [Fact]
    public async Task PlainTextSkipsBlankLinesAndHonorsLineCount()
    {
        var text = new MemoryStream(Encoding.UTF8.GetBytes("a\n\n  b\r\nc\u0001\nd"));
        Assert.Equal("a\nb\nc", await BookContextReader.ReadLinesAsync(text, "TXT", 3));
        Assert.Null(await BookContextReader.ReadLinesAsync(new MemoryStream(Encoding.UTF8.GetBytes(" \n ")), "MD", 3));
        Assert.Null(await BookContextReader.ReadLinesAsync(new MemoryStream([1, 2, 3]), "PDF", 3));
    }

    [Fact]
    public async Task ComicReturnsFirstImageInNaturalOrder()
    {
        var cbz = Zip(("__MACOSX/page1.jpg", Jpeg), ("notes.txt", "x"u8.ToArray()), ("pages/page10.jpg", Jpeg), ("pages/page2.png", Png));

        var image = await BookContextReader.ReadFirstComicImageAsync(new NonSeekableStream(cbz), "CBZ");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Png, image.Bytes);
        Assert.Null(await BookContextReader.ReadFirstComicImageAsync(new MemoryStream(cbz), "CBR"));
    }

    [Fact]
    public async Task ComicRejectsOversizedOrNonImagePages()
    {
        Assert.Null(await BookContextReader.ReadFirstComicImageAsync(new MemoryStream(Zip(("1.jpg", new byte[BookContextReader.MaximumImageBytes + 1]))), "CBZ"));
        Assert.Null(await BookContextReader.ReadFirstComicImageAsync(new MemoryStream(Zip(("1.jpg", "<html>"u8.ToArray()))), "CBZ"));
    }

    private static byte[] Zip(params (string Name, string Content)[] entries) =>
        Zip(entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
