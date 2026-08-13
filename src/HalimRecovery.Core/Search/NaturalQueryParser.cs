using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.Search;

public sealed class FileFilter
{
    public string? NameContains { get; set; }
    public FileCategory? Category { get; set; }
    public DateTime? ModifiedAfterUtc { get; set; }
    public DateTime? ModifiedBeforeUtc { get; set; }
    public RecoveryHealth? MinHealth { get; set; }
    public List<string> Extensions { get; } = new();

    public bool Matches(RecoverableFile f)
    {
        if (NameContains != null &&
            !f.FileName.Contains(NameContains, StringComparison.OrdinalIgnoreCase) &&
            !f.OriginalPath.Contains(NameContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (Category != null && f.Category != Category) return false;
        if (Extensions.Count > 0 && !Extensions.Contains(f.Extension)) return false;
        var date = f.ModifiedUtc ?? f.CreatedUtc;
        if (ModifiedAfterUtc != null && (date == null || date < ModifiedAfterUtc)) return false;
        if (ModifiedBeforeUtc != null && (date == null || date > ModifiedBeforeUtc)) return false;
        if (MinHealth != null && f.Health > MinHealth) return false; // enum order: Green < Yellow < Red
        return true;
    }
}

/// <summary>
/// Offline natural-language search: converts phrases like
/// "geçen ay sildiğim tatil fotoğrafları" or "invoice PDFs from last month"
/// into a deterministic FileFilter. Purely rule-based — no cloud, no AI dependency;
/// recovery works identically without it.
/// </summary>
public static class NaturalQueryParser
{
    public static FileFilter Parse(string query, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var filter = new FileFilter();
        string q = query.ToLowerInvariant();

        // --- category words (Turkish + English) ---
        if (ContainsAny(q, "fotoğraf", "foto", "resim", "görsel", "photo", "picture", "image"))
            filter.Category = FileCategory.Image;
        else if (ContainsAny(q, "video", "film", "movie", "klip"))
            filter.Category = FileCategory.Video;
        else if (ContainsAny(q, "müzik", "ses", "şarkı", "music", "audio", "song"))
            filter.Category = FileCategory.Audio;
        else if (ContainsAny(q, "belge", "doküman", "document", "evrak"))
            filter.Category = FileCategory.Document;
        else if (ContainsAny(q, "arşiv", "archive", "sıkıştırılmış"))
            filter.Category = FileCategory.Archive;

        // --- explicit extensions ---
        foreach (var ext in new[] { "jpg", "jpeg", "png", "gif", "pdf", "docx", "xlsx", "pptx", "zip", "mp4", "mov", "mp3", "wav", "txt" })
            if (q.Contains(ext)) filter.Extensions.Add(ext);
        if (filter.Extensions.Count > 0) filter.Category = null; // extensions are more specific

        // --- time ranges ---
        if (ContainsAny(q, "bugün", "today"))
            filter.ModifiedAfterUtc = now.Date;
        else if (ContainsAny(q, "dün", "yesterday"))
        { filter.ModifiedAfterUtc = now.Date.AddDays(-1); filter.ModifiedBeforeUtc = now.Date; }
        else if (ContainsAny(q, "geçen hafta", "last week"))
            filter.ModifiedAfterUtc = now.Date.AddDays(-14);
        else if (ContainsAny(q, "geçen ay", "last month"))
            filter.ModifiedAfterUtc = now.Date.AddMonths(-2);
        else if (ContainsAny(q, "geçen yıl", "last year"))
            filter.ModifiedAfterUtc = now.Date.AddYears(-2);

        // Year mentions: "2025", optionally with season/summer keywords.
        for (int year = 2000; year <= now.Year + 1; year++)
        {
            if (!q.Contains(year.ToString())) continue;
            var (from, to) = SeasonRange(q, year);
            filter.ModifiedAfterUtc = from;
            filter.ModifiedBeforeUtc = to;
            break;
        }

        // --- name keywords: words not consumed by known patterns, e.g. "fatura", "invoice", "tatil" ---
        var stop = new HashSet<string> { "geçen", "ay", "hafta", "yıl", "sildiğim", "silinen", "bul", "göster", "dosya", "dosyaları",
            "adında", "geçen", "isimli", "oluşturduğum", "the", "a", "in", "with", "name", "named", "files", "find", "show", "deleted",
            "my", "from", "last", "month", "week", "year", "bugün", "dün", "today", "yesterday", "ve", "and",
            "fotoğraf", "fotoğrafları", "fotoğraflarını", "foto", "resim", "görsel", "photo", "photos", "picture", "pictures", "image", "images",
            "video", "videoları", "videolar", "film", "movie", "klip", "müzik", "ses", "şarkı", "music", "audio", "song",
            "belge", "doküman", "document", "documents", "evrak", "arşiv", "archive",
            "jpg", "jpeg", "png", "gif", "pdf", "docx", "xlsx", "pptx", "zip", "mp4", "mov", "mp3", "wav", "txt",
            "pdfleri", "pdf'leri", "pdfler", "yazında", "kışında", "baharında" };
        var words = q.Split([' ', ',', '.', '\'', '"', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stop.Contains(w) && !w.All(char.IsDigit))
            .ToList();
        if (words.Count > 0) filter.NameContains = words[0];

        return filter;
    }

    private static (DateTime From, DateTime To) SeasonRange(string q, int year)
    {
        if (ContainsAny(q, "yaz", "summer")) return (new DateTime(year, 6, 1), new DateTime(year, 9, 1));
        if (ContainsAny(q, "kış", "winter")) return (new DateTime(year - 1, 12, 1), new DateTime(year, 3, 1));
        if (ContainsAny(q, "bahar", "spring")) return (new DateTime(year, 3, 1), new DateTime(year, 6, 1));
        if (ContainsAny(q, "sonbahar", "güz", "autumn", "fall")) return (new DateTime(year, 9, 1), new DateTime(year, 12, 1));
        return (new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1));
    }

    private static bool ContainsAny(string q, params string[] words) => words.Any(q.Contains);
}
