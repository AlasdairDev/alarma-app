// crash log, encrypted. used to be AES-CBC under a hardcoded key - bad, anyone could decompile
// the apk and read it, plus no integrity. now AES-256-GCM with a random key in the Keystore,
// same as the db + backup.
// heads up: Debug.WriteLine is compiled out of Release, so the old reader basically deleted the
// log and threw it away. now we write the decrypted report to app_fault_report.txt in the cache
// dir so it's actually readable on a real build.
// WriteCrashLog can't throw (process is dying when it runs) so we preload the key at startup and
// encrypt synchronously.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

/// <summary>
/// On-device encrypted crash log. survives a force-kill and gets dumped to a readable report on
/// the next launch so we can see what killed the app - works in Release too (no Debug output there).
/// </summary>
public static class BlackBoxLogger
{
    private const string KeyName = "alarma_blackbox_key_v2"; // v2 = AES-GCM Keystore slot
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;

    // key loaded once from the Keystore at startup and cached so the crash handler can encrypt
    // without awaiting. null until LoadKeyAsync runs (or if SecureStorage dies) - then we just no-op.
    private static byte[]? _aesKey;

    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "alarma_blackbox_crashlog.bin");

    /// <summary>
    /// Readable fault report written on the next launch after a crash. survives Release (unlike
    /// Debug.WriteLine). pull it via adb or show it in-app.
    /// </summary>
    public static string FaultReportPath =>
        Path.Combine(FileSystem.CacheDirectory, "app_fault_report.txt");

    /// <summary>set by HomeController on every GPS fix.</summary>
    public static (double Lat, double Lon)? LastKnownCoords { get; set; }

    /// <summary>
    /// loads/creates the key then dumps any previous crash to the report file. call once at startup.
    /// </summary>
    public static async Task InitializeAndReportAsync()
    {
        await LoadKeyAsync();
        CheckAndReportPreviousCrash();
    }

    private static async Task LoadKeyAsync()
    {
        if (_aesKey is not null) return;
        try
        {
            var existing = await SecureStorage.GetAsync(KeyName);
            if (!string.IsNullOrEmpty(existing))
            {
                _aesKey = Convert.FromBase64String(existing);
                return;
            }

            var key = RandomNumberGenerator.GetBytes(32);
            await SecureStorage.SetAsync(KeyName, Convert.ToBase64String(key));
            _aesKey = key;
        }
        catch
        {
            // SecureStorage flakes out on some devices/emulators - just disable logging instead of
            // crashing. _aesKey stays null and everything no-ops.
        }
    }

    /// <summary>
    /// encrypts + writes a fatal crash record. must never throw - it runs from the terminal
    /// exception handler when the process is about to die.
    /// </summary>
    public static void WriteCrashLog(Exception? ex, string source) =>
        WriteEntry(FormatEntry(ex, source, "CRASH"));

    /// <summary>
    /// logs a handled (non-fatal) exception so caught errors actually show up in prod instead of
    /// vanishing into a Debug.WriteLine that doesn't exist in Release.
    /// </summary>
    public static void RecordHandledException(Exception? ex, string source) =>
        WriteEntry(FormatEntry(ex, source, "HANDLED"));

    private static string FormatEntry(Exception? ex, string source, string kind)
    {
        var coords = LastKnownCoords;
        return
            $"TIMESTAMP  : {DateTimeOffset.UtcNow:O}\r\n" +
            $"KIND       : {kind}\r\n" +
            $"SOURCE     : {source}\r\n" +
            $"LAST COORDS: {(coords.HasValue ? $"{coords.Value.Lat:F6}, {coords.Value.Lon:F6}" : "unavailable")}\r\n" +
            $"EXCEPTION  : {ex?.GetType().FullName ?? "unknown"}\r\n" +
            $"MESSAGE    : {ex?.Message ?? "(none)"}\r\n" +
            $"STACK TRACE:\r\n{ex?.StackTrace ?? "(no stack trace available)"}";
    }

    // layout: [12-byte nonce][16-byte GCM tag][ciphertext]. GCM gives us both encryption and
    // integrity - a tampered file fails auth on decrypt before any plaintext comes back.
    private static void WriteEntry(string entry)
    {
        try
        {
            var key = _aesKey;
            if (key is null) return; // key not loaded / SecureStorage unavailable — no-op safely

            var plain = Encoding.UTF8.GetBytes(entry);
            var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[GcmTagSize];

            using (var aes = new AesGcm(key, GcmTagSize))
                aes.Encrypt(nonce, plain, cipher, tag);

            var output = new byte[GcmNonceSize + GcmTagSize + cipher.Length];
            nonce.CopyTo(output, 0);
            tag.CopyTo(output, GcmNonceSize);
            cipher.CopyTo(output, GcmNonceSize + GcmTagSize);
            File.WriteAllBytes(LogPath, output);
        }
        catch { /* never throw out of here */ }
    }

    /// <summary>
    /// runs on launch (via <see cref="InitializeAndReportAsync"/>). if there's a crash log we
    /// decrypt it, write it to <see cref="FaultReportPath"/>, then delete the source. read once.
    /// </summary>
    public static void CheckAndReportPreviousCrash()
    {
        try
        {
            if (!File.Exists(LogPath)) return;

            var key = _aesKey;
            var data = File.ReadAllBytes(LogPath);
            File.Delete(LogPath);

            if (key is null || data.Length < GcmNonceSize + GcmTagSize + 1) return;

            var nonce = data[..GcmNonceSize];
            var tag = data[GcmNonceSize..(GcmNonceSize + GcmTagSize)];
            var cipher = data[(GcmNonceSize + GcmTagSize)..];
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(key, GcmTagSize))
                aes.Decrypt(nonce, cipher, tag, plain);

            var log = Encoding.UTF8.GetString(plain);
            var report =
                "──────────── ALARMA FAULT REPORT ────────────\r\n" +
                $"RECOVERED AT: {DateTimeOffset.UtcNow:O}\r\n" +
                "──────────────────────────────────────────────\r\n" +
                log + "\r\n";

            // write the readable file first (survives Release), then echo to debug.
            File.WriteAllText(FaultReportPath, report);
            System.Diagnostics.Debug.WriteLine($"[ALARMA BLACK BOX] Previous crash recovered to: {FaultReportPath}");
            System.Diagnostics.Debug.WriteLine(report);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ALARMA BLACK BOX] Failed to read crash log: {ex.Message}");
        }
    }
}
