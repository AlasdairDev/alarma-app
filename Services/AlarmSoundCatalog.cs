namespace AlarmaApp.Services;

// One dependency-free place that knows which alarm-sound keys are legal and how a messy/missing one
// gets normalised back to something the alarm can actually play. The audio service, the controller's
// whitelist, the per-stage resolver and the backup-restore validator all defer to this so they can
// never drift apart and let a saved preference point at a sound that no longer exists.
//
// It's intentionally pure C# (no Android, no MAUI) — the AlarmaApp.Tests project can't reference this
// android-targeted assembly, so AlarmSoundCatalogTests re-implements these exact rules and constants.
// Keep the two in lock-step when you touch anything here.
public static class AlarmSoundCatalog
{
    // The voice every fallback lands on — a real bundled res/raw asset, so it's guaranteed to exist.
    public const string Default = "Digital Clock";

    // Sentinel keys that aren't bundled voices: the user's imported file, and "no sound at all".
    public const string CustomKey = "Custom";
    public const string NoneKey = "None";

    // The five bundled voices, in display order — matches the res/raw assets and the Settings picker.
    public static readonly IReadOnlyList<string> BundledSounds =
        new[] { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };

    private static readonly HashSet<string> BundledSet =
        new(BundledSounds, StringComparer.OrdinalIgnoreCase);

    // ── Import validation knobs (Feature 1) ──────────────────────────────────
    // Audio-only file types the alarm loop can actually decode and ring on the alarm channel.
    public static readonly IReadOnlyList<string> AllowedExtensions =
        new[] { ".mp3", ".ogg", ".wav", ".m4a", ".aac" };
    // A sane ceiling so a rider can't copy a multi-hundred-MB file into app-private storage by accident.
    public const long MaxImportBytes = 10L * 1024 * 1024; // 10 MB
    // The clip has to be long enough to be a usable alarm tone but not an entire podcast.
    public const double MinDurationSeconds = 0.5;
    public const double MaxDurationSeconds = 300.0; // 5 minutes

    public static bool IsBundled(string? key)
        => key is not null && BundledSet.Contains(key.Trim());

    /// <summary>
    /// Canonicalise a stored/restored key to a guaranteed-playable one:
    ///   blank / unknown / retired  -> <see cref="Default"/>;
    ///   "None"   -> kept only when <paramref name="allowNone"/> (else Default);
    ///   "Custom" -> kept only when <paramref name="customAvailable"/> (else Default);
    ///   a bundled voice -> its canonical-cased name.
    /// </summary>
    public static string Normalize(string? key, bool customAvailable, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(key)) return Default;
        var trimmed = key.Trim();

        if (trimmed.Equals(NoneKey, StringComparison.OrdinalIgnoreCase))
            return allowNone ? NoneKey : Default;

        if (trimmed.Equals(CustomKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomKey : Default;

        foreach (var bundled in BundledSounds)
            if (bundled.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return bundled;

        return Default;
    }

    public static bool IsExtensionAllowed(string? fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension)) return false;
        var ext = fileNameOrExtension.Contains('.')
            ? System.IO.Path.GetExtension(fileNameOrExtension)
            : "." + fileNameOrExtension.TrimStart('.');
        foreach (var allowed in AllowedExtensions)
            if (allowed.Equals(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool IsSizeAllowed(long sizeBytes)
        => sizeBytes > 0 && sizeBytes <= MaxImportBytes;

    public static bool IsDurationAllowed(double seconds)
        => seconds >= MinDurationSeconds && seconds <= MaxDurationSeconds;

    /// <summary>
    /// Pre-copy import gate (extension + size). Returns the inline-feedback message for the first
    /// failure, or null when the file passes. Duration is checked separately once the file is on disk
    /// and can be probed by the platform decoder.
    /// </summary>
    public static string? ValidateImport(string? fileName, long sizeBytes)
    {
        if (!IsExtensionAllowed(fileName))
            return "Unsupported file type. Use MP3, OGG, WAV, M4A, or AAC.";
        if (!IsSizeAllowed(sizeBytes))
            return $"File is too large. Keep it under {MaxImportBytes / (1024 * 1024)} MB.";
        return null;
    }
}
