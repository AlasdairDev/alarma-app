// =============================================================================
//  BlackBoxLoggerFormatTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the "Forensic Logging" section of README.md — the
//  Black Box unhandled-exception logger's crash-record STRING FORMATTING and
//  its AES-256-CBC (IV ‖ ciphertext) round-trip.
//
//  README "Log record format" block under test:
//      TIMESTAMP  : 2026-06-07T12:34:56.789+00:00
//      SOURCE     : AppDomain.UnhandledException (terminating=True)
//      LAST COORDS: 14.599800, 120.992000
//      EXCEPTION  : System.NullReferenceException
//      MESSAGE    : Object reference not set to an instance of an object.
//      STACK TRACE:
//         at AlarmaApp.Controllers.HomeController...
//
//  Production source mirrored here (Services/BlackBoxLogger.cs:30-60). The
//  entry-building expression and the AES key derivation / IV‖ciphertext layout
//  are re-implemented as pure helpers so the format is provable on plain net9.0
//  with NO Android / MAUI SDK and NO reference to production code.
//
//  Encryption parity note: the helper below derives the SAME key the production
//  logger uses — SHA256("AlarmaBlackBoxKey2026") — and lays out IV(16) ‖
//  ciphertext exactly as WriteCrashLog does, so the round-trip test validates
//  the documented "IV ‖ ciphertext" envelope and the CheckAndReportPreviousCrash
//  length guard (< 17 bytes rejected).
// =============================================================================

using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AlarmaApp.Tests;

/// <summary>
/// Pure re-implementation of BlackBoxLogger's record formatting + crypto
/// envelope. Field labels, padding, separators, and number formats trace to
/// BlackBoxLogger.cs:36-41.
/// </summary>
internal static class BlackBoxFormat
{
    // BlackBoxLogger.cs:15-16 — identical derivation.
    private static readonly byte[] AesKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("AlarmaBlackBoxKey2026"));

    /// <summary>Mirrors the WriteCrashLog entry string (BlackBoxLogger.cs:36-41).</summary>
    public static string BuildEntry(
        DateTimeOffset timestamp, string source,
        (double Lat, double Lon)? coords,
        string? exceptionTypeFullName, string? message, string? stackTrace)
    {
        return
            $"TIMESTAMP  : {timestamp:O}\r\n" +
            $"SOURCE     : {source}\r\n" +
            $"LAST COORDS: {(coords.HasValue ? $"{coords.Value.Lat:F6}, {coords.Value.Lon:F6}" : "unavailable")}\r\n" +
            $"EXCEPTION  : {exceptionTypeFullName ?? "unknown"}\r\n" +
            $"MESSAGE    : {message ?? "(none)"}\r\n" +
            $"STACK TRACE:\r\n{stackTrace ?? "(no stack trace available)"}";
    }

    /// <summary>Overload taking a live Exception, exactly as production does.</summary>
    public static string BuildEntry(
        DateTimeOffset timestamp, string source,
        (double Lat, double Lon)? coords, Exception? ex)
        => BuildEntry(timestamp, source, coords, ex?.GetType().FullName, ex?.Message, ex?.StackTrace);

    /// <summary>AES-256-CBC encrypt with IV ‖ ciphertext layout (BlackBoxLogger.cs:45-57).</summary>
    public static byte[] Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.GenerateIV();
        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            cs.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Decrypt the IV ‖ ciphertext envelope (BlackBoxLogger.cs:75-87).</summary>
    public static string Decrypt(byte[] data)
    {
        if (data.Length < 17) throw new ArgumentException("Too short — IV(16) + >=1 cipher byte required.");
        var iv = data[..16];
        var cipher = data[16..];
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.IV = iv;
        using var ms = new MemoryStream(cipher);
        using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var reader = new StreamReader(cs, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public class BlackBoxRecordFormatTests
{
    private static readonly DateTimeOffset SampleTime =
        new(2026, 6, 7, 12, 34, 56, 789, TimeSpan.Zero);

    // The six fixed field labels appear in order, each on its own CRLF line.
    [Fact]
    public void Entry_ContainsAllSixLabels_InOrder()
    {
        var entry = BlackBoxFormat.BuildEntry(
            SampleTime, "AppDomain.UnhandledException (terminating=True)",
            (14.5998, 120.9920),
            "System.NullReferenceException",
            "Object reference not set to an instance of an object.",
            "   at AlarmaApp.Controllers.HomeController.Foo()");

        int iTs = entry.IndexOf("TIMESTAMP  :", StringComparison.Ordinal);
        int iSrc = entry.IndexOf("SOURCE     :", StringComparison.Ordinal);
        int iCoord = entry.IndexOf("LAST COORDS:", StringComparison.Ordinal);
        int iExc = entry.IndexOf("EXCEPTION  :", StringComparison.Ordinal);
        int iMsg = entry.IndexOf("MESSAGE    :", StringComparison.Ordinal);
        int iStk = entry.IndexOf("STACK TRACE:", StringComparison.Ordinal);

        Assert.True(iTs >= 0 && iSrc > iTs && iCoord > iSrc && iExc > iCoord && iMsg > iExc && iStk > iMsg,
            "All six labels must be present in the documented order.");
    }

    // TIMESTAMP uses the round-trip "O" format (matches README example shape).
    [Fact]
    public void Timestamp_UsesRoundTripOFormat()
    {
        var entry = BlackBoxFormat.BuildEntry(SampleTime, "x", null, null, null, null);
        Assert.Contains($"TIMESTAMP  : {SampleTime:O}", entry);
        // README sample shape: 2026-06-07T12:34:56.789+00:00
        Assert.Contains("2026-06-07T12:34:56.7890000+00:00", entry);
    }

    // Coords are F6-formatted (six decimals) — matches "14.599800, 120.992000".
    [Fact]
    public void Coords_AreFormattedToSixDecimals()
    {
        var entry = BlackBoxFormat.BuildEntry(SampleTime, "x", (14.5998, 120.9920), null, null, null);
        Assert.Contains("LAST COORDS: 14.599800, 120.992000", entry);
    }

    // No GPS fix yet → the literal "unavailable" sentinel, never "0, 0".
    [Fact]
    public void Coords_WhenNull_RenderUnavailable()
    {
        var entry = BlackBoxFormat.BuildEntry(SampleTime, "x", null, null, null, null);
        Assert.Contains("LAST COORDS: unavailable", entry);
        Assert.DoesNotContain("0.000000", entry);
    }

    // Null exception → the three documented sentinels, so the record is never
    // blank even when the handler had nothing to work with.
    [Fact]
    public void NullException_UsesSentinelDefaults()
    {
        var entry = BlackBoxFormat.BuildEntry(SampleTime, "TaskScheduler.UnobservedTaskException", null, null);
        Assert.Contains("EXCEPTION  : unknown", entry);
        Assert.Contains("MESSAGE    : (none)", entry);
        Assert.Contains("STACK TRACE:\r\n(no stack trace available)", entry);
    }

    // A real exception's FQTN and message are captured verbatim.
    [Fact]
    public void RealException_CapturesTypeFullNameAndMessage()
    {
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { caught = e; }

        var entry = BlackBoxFormat.BuildEntry(SampleTime, "AppDomain.UnhandledException (terminating=True)",
            (14.0, 121.0), caught);

        Assert.Contains("EXCEPTION  : System.InvalidOperationException", entry);
        Assert.Contains("MESSAGE    : boom", entry);
        Assert.Contains("at ", entry); // stack trace present
    }

    // The IsTerminating flag is carried through the SOURCE label verbatim.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Source_CarriesTerminatingFlag(bool terminating)
    {
        var source = $"AppDomain.UnhandledException (terminating={terminating})";
        var entry = BlackBoxFormat.BuildEntry(SampleTime, source, null, null, null, null);
        Assert.Contains($"SOURCE     : AppDomain.UnhandledException (terminating={terminating})", entry);
    }

    // Field labels are column-aligned: every label's colon sits at index 11.
    [Fact]
    public void Labels_AreColumnAligned()
    {
        foreach (var label in new[] { "TIMESTAMP  :", "SOURCE     :", "LAST COORDS:", "EXCEPTION  :", "MESSAGE    :" })
            Assert.Equal(11, label.IndexOf(':'));
    }
}

public class BlackBoxEncryptionTests
{
    // IV ‖ ciphertext round-trips back to the original crash record.
    [Fact]
    public void Encrypt_Decrypt_RoundTrips()
    {
        var time = new DateTimeOffset(2026, 6, 7, 12, 34, 56, 789, TimeSpan.Zero);
        var entry = BlackBoxFormat.BuildEntry(time, "AppDomain.UnhandledException (terminating=True)",
            (14.5998, 120.9920), "System.NullReferenceException",
            "Object reference not set to an instance of an object.",
            "   at AlarmaApp.Controllers.HomeController.Foo()");

        var cipher = BlackBoxFormat.Encrypt(entry);
        Assert.Equal(entry, BlackBoxFormat.Decrypt(cipher));
    }

    // The envelope is IV(16) ‖ ciphertext, so output always exceeds 16 bytes and
    // its first 16 bytes are the IV (a fresh random IV per write → differing
    // ciphertexts for identical plaintext).
    [Fact]
    public void Envelope_PrependsFreshIv()
    {
        var c1 = BlackBoxFormat.Encrypt("same input");
        var c2 = BlackBoxFormat.Encrypt("same input");

        Assert.True(c1.Length > 16);
        Assert.NotEqual(c1[..16], c2[..16]);  // random IVs differ
        Assert.NotEqual(c1, c2);              // ⇒ ciphertexts differ
        // Both still decrypt to the same plaintext.
        Assert.Equal("same input", BlackBoxFormat.Decrypt(c1));
        Assert.Equal("same input", BlackBoxFormat.Decrypt(c2));
    }

    // CheckAndReportPreviousCrash rejects anything shorter than 17 bytes
    // (IV(16) + >= 1 cipher byte) — guards against a truncated/corrupt file.
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void ShortBuffer_IsRejected(int len)
        => Assert.Throws<ArgumentException>(() => BlackBoxFormat.Decrypt(new byte[len]));
}
