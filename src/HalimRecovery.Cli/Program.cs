using System.Text.Json;
using HalimRecovery.Core.Disks;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;
using HalimRecovery.Core.Recovery;
using HalimRecovery.Core.Reporting;
using HalimRecovery.Core.Scanning;

const string Version = "0.5.0";

if (args.Length == 0)
{
    Console.WriteLine($"""
        HALIM RECOVERY CLI v{Version} — Free & Open Source Windows Data Recovery
        (c) Halim Toklu — https://github.com/

        USAGE:
          HalimRecovery.Cli list-disks
          HalimRecovery.Cli quick-scan <drive> [--json <out.json>]
          HalimRecovery.Cli deep-scan  <drive> [--json <out.json>]
          HalimRecovery.Cli recover    <drive> --dest <folder> [--mode quick|deep] [--filter <name>] [--all]

        NOTES:
          - Raw volume access requires Administrator rights.
          - The source volume is only ever read. Recover to a DIFFERENT drive whenever possible.
          - SSDs with TRIM may have already erased deleted data; no tool can recover that.
        """);
    return 0;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "list-disks": return ListDisks();
        case "quick-scan": return await Scan(args, deep: false);
        case "deep-scan": return await Scan(args, deep: true);
        case "recover": return await Recover(args);
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            return 2;
    }
}
catch (UnauthorizedAccessException ex)
{
    Console.Error.WriteLine($"ACCESS DENIED: {ex.Message}");
    Console.Error.WriteLine("Run this tool from an elevated (Administrator) prompt.");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Log.Error("Cli", "Unhandled error", ex);
    return 1;
}

static int ListDisks()
{
    foreach (var disk in DiskEnumerator.GetDisks())
    {
        Console.WriteLine($"Disk {disk.Index}: {disk.Model} — {disk.SizeBytes / (1UL << 30)} GiB {(disk.IsSsd ? "[SSD]" : "")}");
        foreach (var v in disk.Volumes)
            Console.WriteLine($"   {v.DriveLetter}: [{v.FileSystemName}] \"{v.Label}\" {v.SizeBytes / (1UL << 20)} MiB");
    }
    return 0;
}

static async Task<int> Scan(string[] args, bool deep)
{
    if (args.Length < 2) { Console.Error.WriteLine("Missing drive letter."); return 2; }
    string drive = args[1].TrimEnd(':', '\\');
    string? jsonOut = ArgValue(args, "--json");

    using var engine = new ScanEngine(drive);
    Console.WriteLine($"Volume {drive}: filesystem = {engine.FileSystem}");
    var progress = new Progress<ScanProgress>(p =>
        Console.Write($"\r{p.Phase}: {p.Percent:F1}% | {p.FilesFound} files | {p.Speed / (1 << 20):F0} MiB/s | elapsed {p.Elapsed:mm\\:ss}   "));

    var files = deep
        ? await engine.DeepScanAsync(progress, CancellationToken.None)
        : await engine.QuickScanAsync(progress, CancellationToken.None);
    Console.WriteLine();

    foreach (var f in files.OrderByDescending(f => f.Confidence).Take(50))
        Console.WriteLine($"  [{f.Health,-6}] {f.Confidence,3}%  {f.Size,12:N0} B  {f.OriginalPath}\\{f.FileName}");
    if (files.Count > 50) Console.WriteLine($"  ... and {files.Count - 50} more");
    Console.WriteLine($"TOTAL: {files.Count} recoverable file(s) found.");

    if (jsonOut != null)
    {
        var dto = files.Select(f => new
        {
            f.Id, f.FileName, f.OriginalPath, f.Size, ext = f.Extension,
            f.Health, f.Confidence, f.Source, notes = f.HealthNotes,
            f.CreatedUtc, f.ModifiedUtc, fragmented = f.IsFragmented
        });
        File.WriteAllText(jsonOut, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"JSON written to {jsonOut}");
    }
    return 0;
}

static async Task<int> Recover(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Missing drive letter."); return 2; }
    string drive = args[1].TrimEnd(':', '\\');
    string? dest = ArgValue(args, "--dest");
    string mode = ArgValue(args, "--mode") ?? "quick";
    string? filter = ArgValue(args, "--filter");
    bool all = args.Contains("--all");
    if (dest == null) { Console.Error.WriteLine("Missing --dest <folder>."); return 2; }

    var (blocked, warning) = RecoveryEngine.CheckDestination(drive, dest);
    if (blocked) { Console.Error.WriteLine($"BLOCKED: {warning}"); return 4; }
    if (warning != null) Console.WriteLine($"WARNING: {warning}");

    using var engine = new ScanEngine(drive);
    var progress = new Progress<ScanProgress>(p =>
        Console.Write($"\r{p.Phase}: {p.Percent:F1}% | {p.FilesFound} files | elapsed {p.Elapsed:mm\\:ss}   "));

    var started = System.Diagnostics.Stopwatch.StartNew();
    var files = mode == "deep"
        ? await engine.DeepScanAsync(progress, CancellationToken.None)
        : await engine.QuickScanAsync(progress, CancellationToken.None);
    Console.WriteLine($"\nFound {files.Count} file(s).");

    var selected = files.Where(f =>
        (all || f.Health != RecoveryHealth.Red) &&
        (filter == null || f.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
    Console.WriteLine($"Recovering {selected.Count} file(s) to {dest} ...");

    var recovery = new RecoveryEngine(engine.Reader, drive);
    var items = await recovery.RecoverAsync(selected, dest, preserveFolderStructure: true, progress, CancellationToken.None);
    Console.WriteLine();

    string report = RecoveryReport.Write(dest, $"{drive}:", items, started.Elapsed);
    int ok = items.Count(i => i.Status == RecoveryStatus.Recovered);
    int partial = items.Count(i => i.Status == RecoveryStatus.PartiallyRecovered);
    int failed = items.Count(i => i.Status == RecoveryStatus.Failed);
    Console.WriteLine($"DONE: {ok} recovered, {partial} partial, {failed} failed. Report: {report}");
    return failed == items.Count && items.Count > 0 ? 5 : 0;
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
