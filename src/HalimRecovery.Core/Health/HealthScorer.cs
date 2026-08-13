using HalimRecovery.Core.Carving;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.Health;

/// <summary>
/// Computes recovery health from measurable evidence only:
///  - cluster reuse (filesystem allocation bitmap/FAT) — reused clusters mean lost data
///  - format signature + structural validation of the actual on-disk bytes
///  - fragmentation and layout certainty
/// No invented percentages: every point of the score maps to a concrete check.
/// </summary>
public static class HealthScorer
{
    public static void Score(RecoverableFile file, IByteSource? content)
    {
        int score;

        // Evidence 1: cluster reuse (55% weight). -1 = unknown.
        double reuse = file.OverwrittenFraction;
        if (reuse < 0)
        {
            score = 25; // unknown allocation state
            file.HealthNotes.Add("Cluster allocation state unknown");
        }
        else
        {
            score = (int)(55 * (1 - reuse));
            if (reuse == 0) file.HealthNotes.Add("No clusters reused by other files");
            else file.HealthNotes.Add($"{reuse:P0} of clusters have been reused — that data is overwritten");
        }

        // Evidence 2: structural validation of actual content (35% weight).
        var validation = ValidateContent(file, content);
        switch (validation)
        {
            case ContentValidation.Valid:
                score += 35;
                file.HealthNotes.Add("File signature and structure verified on disk");
                break;
            case ContentValidation.HeaderOnly:
                score += 15;
                file.HealthNotes.Add("File header matches but full structure could not be verified");
                break;
            case ContentValidation.Mismatch:
                score += 0;
                file.HealthNotes.Add("Content does not match the expected file format (likely overwritten)");
                break;
            case ContentValidation.NotApplicable:
                score += 18; // format unknown to validators: neutral, do not punish or reward
                file.HealthNotes.Add("No structural validator available for this file type");
                break;
        }

        // Evidence 3: layout certainty (10% weight).
        if (file.ResidentData != null)
        {
            score += 10; // data was inside the MFT record itself — exact
            file.HealthNotes.Add("File data resides in filesystem metadata (exact recovery)");
        }
        else if (file.Source == FileSource.FileSystemMetadata &&
                 file.HealthNotes.Any(n => n.Contains("contiguous layout assumed")))
        {
            score += 3; // assumption involved
        }
        else
        {
            score += file.IsFragmented ? 6 : 10;
            if (file.IsFragmented) file.HealthNotes.Add($"File is fragmented ({file.Extents.Count} extents)");
        }

        file.Confidence = Math.Clamp(score, 0, 100);
        file.Health = file.Confidence switch
        {
            >= 75 => RecoveryHealth.Green,
            >= 40 => RecoveryHealth.Yellow,
            _ => RecoveryHealth.Red
        };

        if (reuse >= 0.99) // fully overwritten: force red regardless of other evidence
        {
            file.Health = RecoveryHealth.Red;
            file.Confidence = Math.Min(file.Confidence, 10);
        }
    }

    private enum ContentValidation { Valid, HeaderOnly, Mismatch, NotApplicable }

    private static ContentValidation ValidateContent(RecoverableFile file, IByteSource? content)
    {
        if (content == null || content.Length == 0) return ContentValidation.NotApplicable;

        // Carved files were already structurally parsed during carving.
        if (file.Source == FileSource.Carved)
            return file.HealthNotes.Any(n => n.Contains("structure validated"))
                ? ContentValidation.Valid : ContentValidation.HeaderOnly;

        var spec = FindSpec(file.Extension);
        if (spec == null) return ContentValidation.NotApplicable;

        var header = new byte[16];
        int got = content.ReadAt(0, header, 0, 16);
        if (got < 4) return ContentValidation.Mismatch;

        bool headerOk = spec.Name == "MP4/MOV"
            ? FormatSpecs.LooksLikeMp4(header.AsSpan(0, got))
            : spec.Headers.Any(h => got >= h.Length && header.AsSpan(0, h.Length).SequenceEqual(h));
        if (!headerOk && spec.Name == "MP3" && got >= 4 && FormatSpecs.Mp3FrameLength(header) > 0)
            headerOk = true;
        if (!headerOk) return ContentValidation.Mismatch;

        try
        {
            var measure = spec.Measure(content, 0);
            if (measure == null) return ContentValidation.HeaderOnly;
            // Structure parsed to an end close to the recorded size = strong evidence.
            bool sizePlausible = file.Size <= 0 || Math.Abs(measure.Length - file.Size) <= Math.Max(4096, file.Size / 10);
            return measure.StructureValid && sizePlausible ? ContentValidation.Valid : ContentValidation.HeaderOnly;
        }
        catch
        {
            return ContentValidation.HeaderOnly;
        }
    }

    private static FormatSpec? FindSpec(string extension) => extension switch
    {
        "jpg" or "jpeg" => FormatSpecs.All.First(s => s.Name == "JPEG"),
        "png" => FormatSpecs.All.First(s => s.Name == "PNG"),
        "gif" => FormatSpecs.All.First(s => s.Name == "GIF"),
        "pdf" => FormatSpecs.All.First(s => s.Name == "PDF"),
        "zip" or "docx" or "xlsx" or "pptx" => FormatSpecs.All.First(s => s.Name == "ZIP/Office"),
        "mp4" or "mov" or "m4v" or "m4a" => FormatSpecs.All.First(s => s.Name == "MP4/MOV"),
        "mp3" => FormatSpecs.All.First(s => s.Name == "MP3"),
        "wav" => FormatSpecs.All.First(s => s.Name == "WAV"),
        _ => null
    };
}
