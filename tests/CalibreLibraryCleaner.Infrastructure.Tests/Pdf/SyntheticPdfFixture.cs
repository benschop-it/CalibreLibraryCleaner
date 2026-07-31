using System.IO.Compression;
using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Libraries;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Pdf;

internal sealed class SyntheticPdfFixture : IDisposable
{
    private SyntheticPdfFixture(string root, string path)
    {
        Root = root;
        Path = path;
    }

    public string Root { get; }
    public string Path { get; }
    public string RelativePath => System.IO.Path.GetFileName(Path);

    public static SyntheticPdfFixture CreateText(
        int pageCount = 1,
        string? title = "Synthetic PDF",
        string? author = "Test Author",
        string? text = null,
        bool addImage = false,
        bool imageOnly = false,
        bool addExternalLink = false,
        double width = 595,
        double height = 842,
        int imageCopies = 1,
        int imageWidthSamples = 1,
        int imageHeightSamples = 1)
    {
        string root = CreateRoot();
        string path = System.IO.Path.Combine(root, "synthetic.pdf");
        using PdfDocumentBuilder builder = new();
        builder.DocumentInformation.Title = title;
        builder.DocumentInformation.Author = author;
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        string pageText = text ?? string.Concat(Enumerable.Repeat("Deterministic synthetic digital text for PDF assessment. ", 20));
        byte[] png = CreateTinyPng(imageWidthSamples, imageHeightSamples);
        for (int pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            PdfPageBuilder page = builder.AddPage(width, height);
            if (!imageOnly)
            {
                page.AddText($"Page {pageNumber}. {pageText}", 12, new PdfPoint(36, height - 72), font);
            }

            if (addImage || imageOnly)
            {
                for (int imageIndex = 0; imageIndex < imageCopies; imageIndex++)
                {
                    page.AddPng(png, new PdfRectangle(0, 0, width, height));
                }
            }

            if (addExternalLink)
            {
                page.AddLink("https://invalid.example.test/never-opened", new PdfRectangle(10, 10, 20, 20));
            }
        }

        File.WriteAllBytes(path, builder.Build());
        return new(root, path);
    }

    public static SyntheticPdfFixture CreateBytes(byte[] bytes)
    {
        string root = CreateRoot();
        string path = System.IO.Path.Combine(root, "synthetic.pdf");
        File.WriteAllBytes(path, bytes);
        return new(root, path);
    }

    public static SyntheticPdfFixture CreatePasswordProtectedStub() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Filter /Standard /V 1 /R 2 /Length 40 /O <0000000000000000000000000000000000000000000000000000000000000000> /U <1111111111111111111111111111111111111111111111111111111111111111> /P -4 >>",
        ],
        "/Encrypt 5 0 R /ID [<00112233445566778899AABBCCDDEEFF><00112233445566778899AABBCCDDEEFF>]");

    public static SyntheticPdfFixture CreateUnsupportedEncryptionStub() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Filter /Standard /V 99 /R 99 /Length 256 >>",
        ],
        "/Encrypt 5 0 R /ID [<00112233445566778899AABBCCDDEEFF><00112233445566778899AABBCCDDEEFF>]");

#pragma warning disable CA5351 // PDF Standard Security revision 2 requires MD5; this is synthetic compatibility data only.
    public static SyntheticPdfFixture CreateEmptyPasswordEncryptedStub()
    {
        byte[] padding =
        [
            0x28, 0xbf, 0x4e, 0x5e, 0x4e, 0x75, 0x8a, 0x41,
            0x64, 0x00, 0x4e, 0x56, 0xff, 0xfa, 0x01, 0x08,
            0x2e, 0x2e, 0x00, 0xb6, 0xd0, 0x68, 0x3e, 0x80,
            0x2f, 0x0c, 0xa9, 0xfe, 0x64, 0x53, 0x69, 0x7a,
        ];
        byte[] ownerDigest = MD5.HashData(padding);
        byte[] owner = Rc4(ownerDigest.AsSpan(0, 5), padding);
        byte[] fileId = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        byte[] permissions = BitConverter.GetBytes(-4);
        byte[] keyMaterial = [.. padding, .. owner, .. permissions, .. fileId];
        byte[] encryptionKey = MD5.HashData(keyMaterial).AsSpan(0, 5).ToArray();
        byte[] user = Rc4(encryptionKey, padding);
        return CreatePdfObjects(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
                "<< /Length 0 >>\nstream\n\nendstream",
                $"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P -4 >>",
            ],
            "/Encrypt 5 0 R /ID [<00112233445566778899AABBCCDDEEFF><00112233445566778899AABBCCDDEEFF>]");
    }
#pragma warning restore CA5351

    public static SyntheticPdfFixture CreateZeroPageDocument() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
        ]);

    public static SyntheticPdfFixture CreateCyclicPageTree() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [2 0 R] /Count 1 >>",
        ]);

    public static SyntheticPdfFixture CreateOutlineDocument() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (Synthetic chapter) /Parent 5 0 R /Dest [3 0 R /Fit] >>",
        ]);

    public static SyntheticPdfFixture CreateTwoEntryOutlineDocument() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Outlines /First 6 0 R /Last 7 0 R /Count 2 >>",
            "<< /Title (One) /Parent 5 0 R /Dest [3 0 R /Fit] /Next 7 0 R >>",
            "<< /Title (Two) /Parent 5 0 R /Dest [3 0 R /Fit] /Prev 6 0 R >>",
        ]);

    public static SyntheticPdfFixture CreateInertActiveContentDocument() => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (never executed) >> /URI (https://invalid.example.test/never-opened) /EmbeddedFile 5 0 R /Filespec 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Length 4 >>\nstream\ndata\nendstream",
            "<< /Type /Filespec /F (never-opened.bin) /EF << /F 5 0 R >> >>",
        ]);

    public static SyntheticPdfFixture CreateFakeStructuralMarkerStream()
    {
        string fakeMarkers = string.Join(' ', Enumerable.Repeat("obj /JavaScript /JS /URI /GoToR /EmbeddedFile /Filespec", 100));
        return CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            $"<< /Length {fakeMarkers.Length + 3} >>\nstream\n% {fakeMarkers}\nendstream",
        ]);
    }

    public static SyntheticPdfFixture CreateHiddenTextDocument()
    {
        const string content = "BT /F1 12 Tf 3 Tr 36 770 Td (Invisible but extractable text evidence) Tj ET";
        return CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ]);
    }

    public static SyntheticPdfFixture CreateXmpDocument(string xmp) => CreatePdfObjects(
        [
            "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            $"<< /Type /Metadata /Subtype /XML /Length {System.Text.Encoding.ASCII.GetByteCount(xmp)} >>\nstream\n{xmp}\nendstream",
        ]);

    public PdfInspectionRequest CreateRequest(PdfInspectionLimits? limits = null, string? sha256 = null)
    {
        FileInfo file = new(Path);
        string digest = sha256 ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path))).ToLowerInvariant();
        return new(
            new(1),
            Root,
            Path,
            RelativePath,
            new(file.Length, new(digest)),
            new(file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, (int)file.Attributes),
            limits ?? PdfInspectionLimits.V1);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CalibreLibraryCleaner-PdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static SyntheticPdfFixture CreatePdfObjects(IReadOnlyList<string> objects, string trailerExtra = "")
    {
        using MemoryStream stream = new();
        using StreamWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n");
        writer.Flush();
        List<long> offsets = [0];
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            writer.Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
            writer.Flush();
        }

        long xref = stream.Position;
        writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1))
        {
            writer.Write($"{offset:0000000000} 00000 n \n");
        }

        writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return CreateBytes(stream.ToArray());
    }

    private static byte[] CreateTinyPng(int width, int height)
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] scanlines = new byte[checked(height * (1 + width * 3))];
            for (int row = 0; row < height; row++)
            {
                scanlines[row * (1 + width * 3)] = 0;
                scanlines.AsSpan(row * (1 + width * 3) + 1, width * 3).Fill(255);
            }

            zlib.Write(scanlines);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        byte[] crcInput = [.. typeBytes, .. data];
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        output.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        byte[] state = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        int j = 0;
        for (int index = 0; index < state.Length; index++)
        {
            j = (j + state[index] + key[index % key.Length]) & 255;
            (state[index], state[j]) = (state[j], state[index]);
        }

        byte[] output = new byte[input.Length];
        int x = 0;
        j = 0;
        for (int index = 0; index < input.Length; index++)
        {
            x = (x + 1) & 255;
            j = (j + state[x]) & 255;
            (state[x], state[j]) = (state[j], state[x]);
            output[index] = (byte)(input[index] ^ state[(state[x] + state[j]) & 255]);
        }

        return output;
    }
}
