using System.Windows.Media;
using HalimRecovery.Core.Models;

namespace HalimRecovery.App.ViewModels;

/// <summary>Display wrapper for a RecoverableFile.</summary>
public sealed class FileItemVm(RecoverableFile file)
{
    public RecoverableFile File { get; } = file;

    public string FileName => File.FileName;
    public string OriginalPath => File.OriginalPath;
    public string SizeText => FormatSize(File.Size);
    public string TypeText => File.Extension.Length > 0 ? File.Extension.ToUpperInvariant() : File.Category.ToString();
    public string DateText => (File.ModifiedUtc ?? File.CreatedUtc)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string SourceText => File.Source == FileSource.Carved ? "Deep scan" : "Metadata";
    public int Confidence => File.Confidence;
    public string HealthText => File.Health switch
    {
        RecoveryHealth.Green => "High",
        RecoveryHealth.Yellow => "Partial",
        _ => "Low"
    };
    public Brush HealthBrush => File.Health switch
    {
        RecoveryHealth.Green => new SolidColorBrush(Color.FromRgb(0x37, 0xC8, 0x71)),
        RecoveryHealth.Yellow => new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x3C)),
        _ => new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B))
    };
    public string HealthTooltip => string.Join("\n", File.HealthNotes);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };
}
