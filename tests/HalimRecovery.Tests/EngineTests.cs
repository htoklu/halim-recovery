using HalimRecovery.Core.Health;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Models;
using HalimRecovery.Core.Recovery;
using HalimRecovery.Core.Search;

namespace HalimRecovery.Tests;

public class HealthScorerTests
{
    private static RecoverableFile FileWith(string name, double reuse, byte[]? content = null)
    {
        var f = new RecoverableFile { FileName = name, Size = content?.Length ?? 0, OverwrittenFraction = reuse };
        if (content != null) f.ResidentData = content;
        return f;
    }

    [Fact]
    public void IntactValidatedFile_IsGreen()
    {
        var jpeg = TestData.MinimalJpeg();
        var f = FileWith("photo.jpg", reuse: 0, jpeg);
        HealthScorer.Score(f, new ByteArraySource(jpeg));
        Assert.Equal(RecoveryHealth.Green, f.Health);
        Assert.True(f.Confidence >= 75);
    }

    [Fact]
    public void FullyOverwrittenFile_IsRedWithLowConfidence()
    {
        var f = FileWith("gone.jpg", reuse: 1.0);
        f.Extents.Add(new FileExtent(0, 4096));
        HealthScorer.Score(f, new ByteArraySource(new byte[4096]));
        Assert.Equal(RecoveryHealth.Red, f.Health);
        Assert.True(f.Confidence <= 10);
    }

    [Fact]
    public void ContentMismatch_LowersScore()
    {
        var junk = new byte[2048]; // all zeros: not a JPEG
        var intactJpeg = TestData.MinimalJpeg();

        var bad = FileWith("photo.jpg", reuse: 0, junk);
        HealthScorer.Score(bad, new ByteArraySource(junk));

        var good = FileWith("photo.jpg", reuse: 0, intactJpeg);
        HealthScorer.Score(good, new ByteArraySource(intactJpeg));

        Assert.True(bad.Confidence < good.Confidence);
    }

    [Fact]
    public void UnknownFormat_NeutralScoring()
    {
        var f = FileWith("data.xyz", reuse: 0, new byte[100]);
        HealthScorer.Score(f, new ByteArraySource(new byte[100]));
        Assert.NotEqual(RecoveryHealth.Red, f.Health); // unknown format is not punished as corrupt
    }
}

public class PathSafetyTests
{
    [Theory]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("bad<>name.txt", "bad__name.txt")]
    [InlineData("..\\..\\evil.exe", "____evil.exe")]
    [InlineData("CON", "_CON")]
    [InlineData("", "unnamed")]
    [InlineData("trailing...", "trailing_")]
    public void FileNames_Sanitized(string input, string expected)
        => Assert.Equal(expected, PathSafety.SanitizeFileName(input));

    [Fact]
    public void TraversalSegments_RemovedFromPaths()
    {
        string clean = PathSafety.SanitizeRelativePath(@"docs\..\..\secret\file");
        Assert.DoesNotContain("..", clean);
    }

    [Fact]
    public void PathOutsideRoot_Detected()
    {
        Assert.False(PathSafety.IsInsideRoot(@"C:\Recovered", @"C:\Windows\evil.dll"));
        Assert.True(PathSafety.IsInsideRoot(@"C:\Recovered", @"C:\Recovered\sub\file.txt"));
    }
}

public class ExtentByteSourceTests
{
    [Fact]
    public void ResidentData_ReadDirectly()
    {
        var f = new RecoverableFile { ResidentData = [1, 2, 3, 4, 5], Size = 5 };
        var src = new ExtentByteSource(f, null);
        var buf = new byte[3];
        Assert.Equal(3, src.ReadAt(1, buf, 0, 3));
        Assert.Equal(new byte[] { 2, 3, 4 }, buf);
    }

    [Fact]
    public void EmptyFile_ZeroLength()
    {
        var f = new RecoverableFile { Size = 100 }; // extents missing, no reader
        var src = new ExtentByteSource(f, null);
        Assert.Equal(0, src.Length);
    }
}

public class NaturalQueryParserTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TurkishPhotoQuery_MapsToImageCategoryAndDate()
    {
        var filter = NaturalQueryParser.Parse("Geçen ay sildiğim tatil fotoğraflarını bul", Now);
        Assert.Equal(FileCategory.Image, filter.Category);
        Assert.NotNull(filter.ModifiedAfterUtc);
        Assert.Equal("tatil", filter.NameContains);
    }

    [Fact]
    public void InvoicePdfQuery_MapsToExtensionAndKeyword()
    {
        var filter = NaturalQueryParser.Parse("Adında fatura geçen PDF'leri bul", Now);
        Assert.Contains("pdf", filter.Extensions);
        Assert.Equal("fatura", filter.NameContains);
    }

    [Fact]
    public void SummerVideosQuery_MapsToSeasonRange()
    {
        var filter = NaturalQueryParser.Parse("2025 yazında oluşturduğum videoları göster", Now);
        Assert.Equal(FileCategory.Video, filter.Category);
        Assert.Equal(new DateTime(2025, 6, 1), filter.ModifiedAfterUtc);
        Assert.Equal(new DateTime(2025, 9, 1), filter.ModifiedBeforeUtc);
    }

    [Fact]
    public void Filter_MatchesByAllCriteria()
    {
        var file = new RecoverableFile
        {
            FileName = "fatura-2025.pdf",
            ModifiedUtc = new DateTime(2025, 7, 10),
            Category = FileCategory.Document,
            Health = RecoveryHealth.Green
        };
        var filter = new FileFilter { NameContains = "fatura" };
        filter.Extensions.Add("pdf");
        Assert.True(filter.Matches(file));

        filter.NameContains = "makbuz";
        Assert.False(filter.Matches(file));
    }
}
