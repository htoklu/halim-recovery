using System.Text;
using System.Text.Json;
using HalimRecovery.Core.Models;
using HalimRecovery.Core.Recovery;

namespace HalimRecovery.Core.Reporting;

/// <summary>Writes a recovery session report (text + JSON) into the destination folder.</summary>
public static class RecoveryReport
{
    public static string Write(string destinationFolder, string sourceVolume,
        IReadOnlyList<RecoveredItem> items, TimeSpan elapsed)
    {
        var now = DateTime.Now;
        int ok = items.Count(i => i.Status == RecoveryStatus.Recovered);
        int partial = items.Count(i => i.Status == RecoveryStatus.PartiallyRecovered);
        int failed = items.Count(i => i.Status == RecoveryStatus.Failed);

        var sb = new StringBuilder();
        sb.AppendLine("HALIM RECOVERY — Recovery Report");
        sb.AppendLine($"Date: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Source volume: {sourceVolume}");
        sb.AppendLine($"Destination: {destinationFolder}");
        sb.AppendLine($"Duration: {elapsed:hh\\:mm\\:ss}");
        sb.AppendLine($"Result: {ok} recovered, {partial} partial, {failed} failed (total {items.Count})");
        sb.AppendLine(new string('-', 72));
        foreach (var i in items)
        {
            sb.AppendLine($"[{i.Status}] {i.File.FileName}  ({i.BytesWritten:N0} bytes, confidence {i.File.Confidence}%)");
            if (i.OutputPath != null) sb.AppendLine($"    -> {i.OutputPath}");
            if (i.Sha256 != null) sb.AppendLine($"    SHA-256: {i.Sha256}");
            if (i.Error != null) sb.AppendLine($"    Note: {i.Error}");
        }

        string reportPath = Path.Combine(destinationFolder, $"recovery-report-{now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

        var json = new
        {
            date = now,
            source = sourceVolume,
            destination = destinationFolder,
            durationSeconds = elapsed.TotalSeconds,
            summary = new { recovered = ok, partial, failed, total = items.Count },
            files = items.Select(i => new
            {
                name = i.File.FileName,
                originalPath = i.File.OriginalPath,
                size = i.File.Size,
                status = i.Status.ToString(),
                output = i.OutputPath,
                sha256 = i.Sha256,
                confidence = i.File.Confidence,
                health = i.File.Health.ToString(),
                error = i.Error
            })
        };
        File.WriteAllText(Path.ChangeExtension(reportPath, ".json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return reportPath;
    }
}
