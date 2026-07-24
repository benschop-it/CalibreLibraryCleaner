using System.IO.Compression;
using System.Text;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

internal static class SyntheticEpubBuilder
{
    public static void CreateFromEntries(
        string path,
        IEnumerable<(string Name, string Content, CompressionLevel Compression)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string name, string content, CompressionLevel compression) in entries)
        {
            Write(archive, name, content, compression);
        }
    }

    public static void CreateValid(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        Write(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Write(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Write(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">9780306406157</dc:identifier>
                <dc:title>Synthetic EPUB</dc:title><dc:creator>Test Author</dc:creator>
                <dc:language>en</dc:language><dc:date>2020-01-01</dc:date>
              </metadata>
              <manifest>
                <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="cover" href="cover.png" media-type="image/png" properties="cover-image"/>
              </manifest>
              <spine><itemref idref="chapter"/></spine>
            </package>
            """);
        Write(archive, "OEBPS/nav.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav><a href=\"chapter.xhtml\">Chapter</a></nav></body></html>");
        Write(archive, "OEBPS/chapter.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>{new string('a', 6000)}</p><img src=\"cover.png\"/></body></html>");
        byte[] png = new byte[24] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 3, 32, 0, 0, 2, 88 };
        ZipArchiveEntry cover = archive.CreateEntry("OEBPS/cover.png", CompressionLevel.NoCompression);
        using Stream coverStream = cover.Open();
        coverStream.Write(png);
    }

    private static void Write(ZipArchive archive, string name, string text, CompressionLevel compression = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, compression);
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }
}
