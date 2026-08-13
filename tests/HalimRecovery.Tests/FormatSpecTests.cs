using HalimRecovery.Core.Carving;
using HalimRecovery.Core.IO;

namespace HalimRecovery.Tests;

public class FormatSpecTests
{
    private static CarveMeasure? Measure(string specName, byte[] data)
    {
        var spec = FormatSpecs.All.First(s => s.Name == specName);
        return spec.Measure(new ByteArraySource(data), 0);
    }

    [Fact]
    public void Jpeg_ValidFile_MeasuredExactly()
    {
        var jpeg = TestData.MinimalJpeg();
        var m = Measure("JPEG", jpeg);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(jpeg.Length, m.Length);
    }

    [Fact]
    public void Jpeg_TruncatedFile_NotValidated()
    {
        var jpeg = TestData.MinimalJpeg()[..^2]; // remove EOI
        var m = Measure("JPEG", jpeg);
        Assert.Null(m); // no EOI found -> not carvable as complete file
    }

    [Fact]
    public void Jpeg_WithTrailingGarbage_StopsAtEoi()
    {
        var jpeg = TestData.MinimalJpeg();
        var padded = jpeg.Concat(new byte[500]).ToArray();
        var m = Measure("JPEG", padded);
        Assert.NotNull(m);
        Assert.Equal(jpeg.Length, m.Length);
    }

    [Fact]
    public void Png_ValidFile_MeasuredExactly()
    {
        var png = TestData.MinimalPng();
        var m = Measure("PNG", png);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(png.Length, m.Length);
    }

    [Fact]
    public void Png_CorruptChunkType_Rejected()
    {
        var png = TestData.MinimalPng();
        png[12] = 0x01; // destroy IHDR type byte
        Assert.Null(Measure("PNG", png));
    }

    [Fact]
    public void Gif_ValidFile_MeasuredExactly()
    {
        var gif = TestData.MinimalGif();
        var m = Measure("GIF", gif);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(gif.Length, m.Length);
    }

    [Fact]
    public void Pdf_ValidFile_EndsAtLastEof()
    {
        var pdf = TestData.MinimalPdf();
        var m = Measure("PDF", pdf);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.True(m.Length >= pdf.Length - 1 && m.Length <= pdf.Length);
    }

    [Fact]
    public void Zip_ValidArchive_MeasuredExactly()
    {
        var zip = TestData.ZipWith(("hello.txt", "hello world"));
        var m = Measure("ZIP/Office", zip);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(zip.Length, m.Length);
        Assert.Equal("zip", m.Extension);
    }

    [Fact]
    public void Zip_DocxClassifiedByContent()
    {
        var docx = TestData.ZipWith(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<w:document/>"));
        var m = Measure("ZIP/Office", docx);
        Assert.NotNull(m);
        Assert.Equal("docx", m.Extension);
    }

    [Fact]
    public void Zip_XlsxClassifiedByContent()
    {
        var xlsx = TestData.ZipWith(
            ("[Content_Types].xml", "<Types/>"),
            ("xl/workbook.xml", "<workbook/>"));
        var m = Measure("ZIP/Office", xlsx);
        Assert.NotNull(m);
        Assert.Equal("xlsx", m.Extension);
    }

    [Fact]
    public void Mp4_ValidFile_MeasuredExactly()
    {
        var mp4 = TestData.MinimalMp4();
        var m = Measure("MP4/MOV", mp4);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(mp4.Length, m.Length);
        Assert.Equal("mp4", m.Extension);
    }

    [Fact]
    public void Mp3_ValidFrames_MeasuredExactly()
    {
        var mp3 = TestData.MinimalMp3();
        var m = Measure("MP3", mp3);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(mp3.Length, m.Length);
    }

    [Fact]
    public void Mp3_TooFewFrames_Rejected()
    {
        var mp3 = TestData.MinimalMp3(frames: 2);
        Assert.Null(Measure("MP3", mp3));
    }

    [Fact]
    public void Wav_ValidFile_MeasuredExactly()
    {
        var wav = TestData.MinimalWav();
        var m = Measure("WAV", wav);
        Assert.NotNull(m);
        Assert.True(m.StructureValid);
        Assert.Equal(wav.Length, m.Length);
    }

    [Fact]
    public void Wav_NotWave_Rejected()
    {
        var wav = TestData.MinimalWav();
        wav[8] = (byte)'A'; // break "WAVE"
        Assert.Null(Measure("WAV", wav));
    }
}
