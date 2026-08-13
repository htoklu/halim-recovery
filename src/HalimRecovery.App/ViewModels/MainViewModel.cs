using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalimRecovery.Core.Disks;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;
using HalimRecovery.Core.Preview;
using HalimRecovery.Core.Recovery;
using HalimRecovery.Core.Reporting;
using HalimRecovery.Core.Scanning;
using HalimRecovery.Core.Search;

namespace HalimRecovery.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private ScanEngine? _engine;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _scanWatch = new();

    public MainViewModel()
    {
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.Filter = FilterPredicate;
        RefreshDrives();
    }

    // ---------- navigation ----------
    [ObservableProperty] private bool isHomeVisible = true;
    [ObservableProperty] private bool isResultsVisible;
    public string VersionText => $"v{App.AppVersion}";

    // ---------- drives ----------
    public System.Collections.ObjectModel.ObservableCollection<VolumeInfo> Volumes { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SsdWarningVisible))]
    [NotifyCanExecuteChangedFor(nameof(QuickScanCommand), nameof(DeepScanCommand))]
    private VolumeInfo? selectedVolume;

    public bool SsdWarningVisible => SelectedVolume?.HostDiskIsSsd == true;

    [RelayCommand]
    private void RefreshDrives()
    {
        Volumes.Clear();
        try
        {
            foreach (var v in DiskEnumerator.GetVolumes().Where(v => v.DriveLetter.Length > 0))
                Volumes.Add(v);
            SelectedVolume ??= Volumes.FirstOrDefault(v => !string.Equals(v.DriveLetter, "C", StringComparison.OrdinalIgnoreCase)) ?? Volumes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not list drives: {ex.Message}";
            Log.Error("UI", "Drive enumeration failed", ex);
        }
    }

    // ---------- scan state ----------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickScanCommand), nameof(DeepScanCommand), nameof(CancelScanCommand), nameof(PauseResumeCommand), nameof(RecoverSelectedCommand))]
    private bool isScanning;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private string scanPhase = "";
    [ObservableProperty] private double scanPercent;
    [ObservableProperty] private bool scanIndeterminate;
    [ObservableProperty] private string elapsedText = "";
    [ObservableProperty] private string remainingText = "";
    [ObservableProperty] private string speedText = "";
    [ObservableProperty] private string filesFoundText = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string scanTitle = "";

    private bool CanStartScan() => !IsScanning && SelectedVolume != null;

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task QuickScanAsync() => await RunScanAsync(deep: false);

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task DeepScanAsync() => await RunScanAsync(deep: true);

    private async Task RunScanAsync(bool deep)
    {
        if (SelectedVolume == null) return;
        string drive = SelectedVolume.DriveLetter;

        CleanupEngine();
        Results.Clear();
        SelectedFile = null;
        IsHomeVisible = false;
        IsResultsVisible = true;
        IsScanning = true;
        ScanTitle = deep ? $"Deep Scan — {drive}:" : $"Quick Scan — {drive}:";
        StatusMessage = "";
        _cts = new CancellationTokenSource();
        _scanWatch.Restart();

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanPhase = p.Phase;
            ScanIndeterminate = p.BytesTotal <= 0;
            ScanPercent = p.Percent;
            ElapsedText = p.Elapsed.ToString(@"hh\:mm\:ss");
            RemainingText = p.EstimatedRemaining?.ToString(@"hh\:mm\:ss") ?? "—";
            SpeedText = p.Speed > 0 ? $"{p.Speed / (1 << 20):F0} MB/s" : "—";
            FilesFoundText = $"{p.FilesFound:N0} files";
        });

        try
        {
            _engine = new ScanEngine(drive);
            var files = deep
                ? await _engine.DeepScanAsync(progress, _cts.Token)
                : await _engine.QuickScanAsync(progress, _cts.Token);

            foreach (var f in files.OrderByDescending(f => f.Confidence))
                Results.Add(new FileItemVm(f));
            StatusMessage = Results.Count == 0
                ? "No recoverable deleted files were found on this volume."
                : $"{Results.Count:N0} recoverable file(s) found. Select files and press Recover.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "Administrator rights required", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (NotSupportedException ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "Unsupported filesystem", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
            Log.Error("UI", "Scan failed", ex);
            MessageBox.Show($"Scan failed:\n{ex.Message}", "Halim Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            IsPaused = false;
            _scanWatch.Stop();
        }
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void PauseResume()
    {
        if (_engine == null) return;
        if (IsPaused) { _engine.Pause.Resume(); IsPaused = false; ScanPhase = "Resumed"; }
        else { _engine.Pause.Pause(); IsPaused = true; ScanPhase = "Paused"; }
    }

    [RelayCommand]
    private void BackHome()
    {
        _cts?.Cancel();
        CleanupEngine();
        Results.Clear();
        SelectedFile = null;
        IsResultsVisible = false;
        IsHomeVisible = true;
        RefreshDrives();
    }

    private void CleanupEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }

    // ---------- results & filtering ----------
    public System.Collections.ObjectModel.ObservableCollection<FileItemVm> Results { get; } = new();
    public ICollectionView ResultsView { get; }

    public string[] FilterOptions { get; } =
        ["All", "Images", "Videos", "Documents", "Audio", "Archives", "High Confidence", "Partial", "Corrupted"];

    [ObservableProperty] private string selectedFilterOption = "All";
    partial void OnSelectedFilterOptionChanged(string value) => ResultsView.Refresh();

    [ObservableProperty] private string searchText = "";
    partial void OnSearchTextChanged(string value) { _naturalFilter = null; ResultsView.Refresh(); }

    [ObservableProperty] private string naturalQuery = "";
    private FileFilter? _naturalFilter;

    [RelayCommand]
    private void ApplyNaturalQuery()
    {
        _naturalFilter = string.IsNullOrWhiteSpace(NaturalQuery) ? null : NaturalQueryParser.Parse(NaturalQuery);
        ResultsView.Refresh();
        StatusMessage = _naturalFilter == null ? "" : "Smart filter applied (rule-based, offline).";
    }

    [RelayCommand]
    private void ClearNaturalQuery()
    {
        NaturalQuery = "";
        _naturalFilter = null;
        ResultsView.Refresh();
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not FileItemVm vm) return false;
        var f = vm.File;

        if (SelectedFilterOption switch
            {
                "Images" => f.Category != FileCategory.Image,
                "Videos" => f.Category != FileCategory.Video,
                "Documents" => f.Category != FileCategory.Document,
                "Audio" => f.Category != FileCategory.Audio,
                "Archives" => f.Category != FileCategory.Archive,
                "High Confidence" => f.Health != RecoveryHealth.Green,
                "Partial" => f.Health != RecoveryHealth.Yellow,
                "Corrupted" => f.Health != RecoveryHealth.Red,
                _ => false
            }) return false;

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !f.OriginalPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) return false;

        if (_naturalFilter != null && !_naturalFilter.Matches(f)) return false;
        return true;
    }

    // ---------- preview ----------
    [ObservableProperty] private FileItemVm? selectedFile;
    [ObservableProperty] private BitmapImage? previewImage;
    [ObservableProperty] private string previewText = "";
    [ObservableProperty] private string healthNotesText = "";

    partial void OnSelectedFileChanged(FileItemVm? value)
    {
        PreviewImage = null;
        PreviewText = "";
        HealthNotesText = value == null ? "" : string.Join("\n• ", new[] { "" }.Concat(value.File.HealthNotes)).TrimStart('\n');
        if (value != null) _ = LoadPreviewAsync(value);
        RecoverSelectedCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadPreviewAsync(FileItemVm vm)
    {
        if (_engine == null) { PreviewText = "Preview unavailable (volume closed)."; return; }
        var engine = _engine;
        try
        {
            var result = await Task.Run(() => PreviewProvider.GetPreview(vm.File, engine.Reader));
            if (SelectedFile != vm) return; // stale
            switch (result.Kind)
            {
                case PreviewKind.Image when result.ImageBytes != null:
                    try
                    {
                        var img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = new MemoryStream(result.ImageBytes);
                        img.DecodePixelWidth = 480;
                        img.EndInit();
                        img.Freeze();
                        PreviewImage = img;
                    }
                    catch (Exception ex)
                    {
                        PreviewText = $"Image data is too damaged to decode ({ex.Message}).";
                    }
                    break;
                case PreviewKind.Text or PreviewKind.Metadata:
                    PreviewText = result.Text ?? "";
                    break;
                default:
                    PreviewText = result.FailureReason ?? "No preview available for this file.";
                    break;
            }
        }
        catch (Exception ex)
        {
            PreviewText = $"Preview failed: {ex.Message}";
        }
    }

    // ---------- recovery ----------
    public IList<FileItemVm> GridSelection { get; set; } = [];

    private bool CanRecover() => !IsScanning && _engine != null && Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverSelectedAsync()
    {
        if (_engine == null || SelectedVolume == null) return;
        var selected = GridSelection.Count > 0 ? GridSelection.ToList()
            : ResultsView.Cast<FileItemVm>().ToList();
        if (selected.Count == 0) { StatusMessage = "Nothing to recover."; return; }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a destination folder (use a DIFFERENT drive whenever possible)"
        };
        if (dialog.ShowDialog() != true) return;
        string dest = dialog.FolderName;

        var (blocked, warning) = RecoveryEngine.CheckDestination(SelectedVolume.DriveLetter, dest);
        if (blocked)
        {
            MessageBox.Show(warning, "Unsafe destination", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (warning != null &&
            MessageBox.Show($"{warning}\n\nSOURCE: {SelectedVolume.DriveLetter}:\nDESTINATION: {dest}\n\nContinue anyway?",
                "Same physical disk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (MessageBox.Show(
                $"SOURCE: {SelectedVolume.DriveLetter}:\nDESTINATION: {dest}\nFILES: {selected.Count}\n\nStart recovery?",
                "Confirm recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsScanning = true;
        ScanTitle = "Recovering files…";
        _cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanPhase = p.Phase;
            ScanIndeterminate = false;
            ScanPercent = p.Percent;
            ElapsedText = p.Elapsed.ToString(@"hh\:mm\:ss");
            RemainingText = p.EstimatedRemaining?.ToString(@"hh\:mm\:ss") ?? "—";
            FilesFoundText = $"{p.FilesFound:N0} done";
        });

        try
        {
            var recovery = new RecoveryEngine(_engine.Reader, SelectedVolume.DriveLetter);
            var items = await recovery.RecoverAsync(selected.Select(s => s.File), dest,
                preserveFolderStructure: true, progress, _cts.Token);
            string report = RecoveryReport.Write(dest, $"{SelectedVolume.DriveLetter}:", items, watch.Elapsed);

            int ok = items.Count(i => i.Status == RecoveryStatus.Recovered);
            int partial = items.Count(i => i.Status == RecoveryStatus.PartiallyRecovered);
            int failed = items.Count(i => i.Status == RecoveryStatus.Failed);
            StatusMessage = $"Recovery finished: {ok} recovered, {partial} partial, {failed} failed.";
            if (MessageBox.Show($"{StatusMessage}\n\nReport: {report}\n\nOpen the destination folder?",
                    "Recovery complete", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start("explorer.exe", dest);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Recovery cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Recovery failed: {ex.Message}";
            Log.Error("UI", "Recovery failed", ex);
            MessageBox.Show($"Recovery failed:\n{ex.Message}", "Halim Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ---------- support ----------
    [RelayCommand]
    private void OpenSupport() => new Views.SupportDialog { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void DiskImageInfo() => MessageBox.Show(
        "Disk image support (create a sector-by-sector recovery image of a drive, then scan the image) is planned for the next release.",
        "Disk Image — coming soon", MessageBoxButton.OK, MessageBoxImage.Information);
}
