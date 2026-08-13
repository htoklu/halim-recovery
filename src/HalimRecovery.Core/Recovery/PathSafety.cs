namespace HalimRecovery.Core.Recovery;

/// <summary>Filename/path sanitization: recovered names come from raw disk data and are untrusted.</summary>
public static class PathSafety
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    /// <summary>Makes an untrusted filename safe: strips traversal, invalid chars, reserved names.</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        // Never allow directory separators or traversal in a *file name*.
        name = name.Replace("..", "_");
        var chars = name.Select(c => InvalidChars.Contains(c) || c == '/' || c == '\\' || c < 0x20 ? '_' : c).ToArray();
        string clean = new string(chars).Trim().TrimEnd('.');
        if (clean.Length == 0) return "unnamed";
        string stem = Path.GetFileNameWithoutExtension(clean);
        if (ReservedNames.Contains(stem)) clean = "_" + clean;
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>Sanitizes a recovered directory path (relative), removing traversal segments.</summary>
    public static string SanitizeRelativePath(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(SanitizeFileName);
        return string.Join('\\', parts);
    }

    /// <summary>Verifies that a combined path stays inside the destination root.</summary>
    public static bool IsInsideRoot(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
