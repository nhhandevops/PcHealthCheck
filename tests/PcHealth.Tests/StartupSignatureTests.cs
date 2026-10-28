using System.Runtime.InteropServices;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Startup;
using PcHealth.Modules.Windows.Support;
using Xunit;
using State = PcHealth.Modules.Windows.Support.AuthenticodeVerifier.SignatureState;

namespace PcHealth.Tests;

/// <summary>
/// The startup check (REMEDIES-PLAN Step A rows 3 and 12). Windows' own tray icon read as
/// unsigned on every PC (SIG-001), and "every resolved startup item has a valid publisher
/// signature" was written beside broken and unverifiable signatures (START-002). Each change is
/// locked in both directions: what is genuinely signed now reads signed, and what is not still
/// reads as exactly what it is.
/// </summary>
public sealed class StartupSignatureTests
{
    // --- The verifier, against real files on this PC -------------------------------------------

    private static string WindowsSecurityTrayIcon => Path.Combine(Environment.SystemDirectory, "SecurityHealthSystray.exe");

    /// <summary>A file with its own embedded signature that every machine running these tests has.</summary>
    private static string RuntimeLibrary => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll");

    [Fact]
    public void Windows_Securitys_tray_icon_verifies_through_its_catalog()
    {
        // The file has no embedded signature. Microsoft signs a catalog listing its hash instead,
        // and the old check, which looked only inside the file, called it unsigned on every PC.
        Assert.True(File.Exists(WindowsSecurityTrayIcon));

        var result = AuthenticodeVerifier.Verify(WindowsSecurityTrayIcon);

        Assert.Equal(State.SignedAndValid, result.State);
        Assert.True(result.ViaCatalog);
        Assert.Null(result.Failure);
        Assert.Equal("Microsoft Corporation", result.Signer?.Organization);
    }

    [Fact]
    public void A_file_with_its_own_signature_verifies_and_names_its_signer()
    {
        var result = AuthenticodeVerifier.Verify(RuntimeLibrary);

        Assert.Equal(State.SignedAndValid, result.State);
        Assert.False(result.ViaCatalog);
        Assert.Equal("Microsoft Corporation", result.Signer?.Organization);
        Assert.False(string.IsNullOrWhiteSpace(result.Signer?.CommonName));
    }

    [Fact]
    public void A_file_with_no_signature_anywhere_reads_unsigned_not_unverified()
    {
        // The other direction of the catalog fallback: "no catalog lists this file" is the
        // ordinary answer for every non-Windows file, and must not be mistaken for a failed lookup.
        using var file = TempCopy(null, ".exe");
        File.WriteAllText(file.Path, "not a program, and signed by nobody");

        var result = AuthenticodeVerifier.Verify(file.Path);

        Assert.Equal(State.NotSigned, result.State);
        Assert.Null(result.Failure);
        Assert.Null(result.Signer);
    }

    [Fact]
    public void A_signed_file_that_was_changed_reads_as_broken()
    {
        using var file = TempCopy(RuntimeLibrary, ".dll");
        FlipOneByte(file.Path);

        Assert.Equal(State.Invalid, AuthenticodeVerifier.Verify(file.Path).State);
    }

    [Fact]
    public void A_catalog_vouches_for_the_files_contents_not_for_its_name()
    {
        // An exact copy elsewhere is still the file Microsoft listed...
        using var copy = TempCopy(WindowsSecurityTrayIcon, ".exe");
        Assert.Equal(State.SignedAndValid, AuthenticodeVerifier.Verify(copy.Path).State);

        // ...and one changed byte means no catalog lists it any more.
        FlipOneByte(copy.Path);
        var changed = AuthenticodeVerifier.Verify(copy.Path);
        Assert.Equal(State.NotSigned, changed.State);
        Assert.Null(changed.Failure);
    }

    [Fact]
    public void An_empty_file_reads_unsigned_and_does_not_degrade_the_scan()
    {
        // Windows' catalog hasher refuses an empty file (error 1006, ERROR_FILE_INVALID). That was
        // recorded as a broken verifier, so a 0-byte file named by a Run entry put "some checks
        // could not finish" on a healthy PC. Nothing broke: a file that cannot be hashed is in no catalog.
        using var file = TempCopy(null, ".exe");
        File.WriteAllBytes(file.Path, []);

        AssertUnsignedAndNotAFailure(AuthenticodeVerifier.Verify(file.Path));
    }

    [Fact]
    public void A_truncated_program_reads_unsigned_and_does_not_degrade_the_scan()
    {
        // What a failed update can leave behind. The hasher answers 193 (ERROR_BAD_EXE_FORMAT).
        using var file = TempCopy(null, ".exe");
        var notepad = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        File.WriteAllBytes(file.Path, notepad[..4096]);

        AssertUnsignedAndNotAFailure(AuthenticodeVerifier.Verify(file.Path));
    }

    private static void AssertUnsignedAndNotAFailure(AuthenticodeVerifier.Result result)
    {
        Assert.Equal(State.NotSigned, result.State);
        Assert.Null(result.Failure);

        var (_, _, failures) = Report(Item("Leftover", result.State, result));
        Assert.False(failures.Any);
    }

    [Theory]
    [InlineData(0, State.SignedAndValid)]
    [InlineData(unchecked((int)0x800B0100), State.NotSigned)]   // TRUST_E_NOSIGNATURE
    [InlineData(unchecked((int)0x80096010), State.Invalid)]     // TRUST_E_BAD_DIGEST
    [InlineData(unchecked((int)0x800B0111), State.Invalid)]     // TRUST_E_EXPLICIT_DISTRUST
    [InlineData(unchecked((int)0x800B0101), State.Expired)]     // CERT_E_EXPIRED: not tampering
    [InlineData(unchecked((int)0x800B0109), State.Unknown)]     // CERT_E_UNTRUSTEDROOT
    [InlineData(unchecked((int)0x800B010A), State.Unknown)]     // CERT_E_CHAINING
    public void Each_answer_from_Windows_means_one_thing(int status, State expected)
    {
        Assert.Equal(expected, AuthenticodeVerifier.Classify(status));
    }

    // --- The report: the sentence beside the count ----------------------------------------------

    private static readonly AuthenticodeVerifier.Signer Publisher = new("Example Publisher LLC", "Example Publisher LLC");

    private static StartupItem Item(string name, State state, AuthenticodeVerifier.Result? verification = null,
                                    string? path = null, bool disabled = false, bool resolved = true) =>
        new(name, "HKCU Run", name + ".exe", resolved ? path ?? $@"D:\Tools\{name}.exe" : null, state, null, disabled)
        {
            Verification = verification ?? new AuthenticodeVerifier.Result(state, state == State.NotSigned ? null : Publisher),
        };

    private static (List<Metric> Metrics, List<string> Notes, ModuleFailures Failures) Report(params StartupItem[] items)
    {
        var metrics = new List<Metric>();
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        StartupReport.Describe(items, metrics, failures);
        return (metrics, notes, failures);
    }

    private static Metric Row(List<Metric> metrics, string id) => Assert.Single(metrics, m => m.Id == id);

    private const string AllValid = "Every resolved startup item has a valid publisher signature.";

    [Fact]
    public void When_every_item_verified_the_report_says_so()
    {
        var (metrics, notes, failures) = Report(Item("Alpha", State.SignedAndValid), Item("Beta", State.SignedAndValid));

        var unsigned = Row(metrics, "startup.unsigned_count");
        Assert.Equal(0d, unsigned.Value.Number);
        Assert.Equal(AllValid, unsigned.Detail);
        Assert.Empty(notes);
        Assert.False(failures.Any);
    }

    [Fact]
    public void A_broken_signature_with_nothing_unsigned_is_not_called_valid()
    {
        // StartupModule.cs:83-84 checked only the unsigned count, so this wrote "every item has
        // a valid publisher signature" one row above "1 item with a BROKEN signature".
        var (metrics, _, _) = Report(Item("Alpha", State.SignedAndValid), Item("Tampered", State.Invalid));

        Assert.DoesNotContain("valid publisher signature", Row(metrics, "startup.unsigned_count").Detail, StringComparison.Ordinal);
        Assert.Contains("BROKEN", Row(metrics, "startup.unsigned_count").Detail, StringComparison.Ordinal);
        Assert.Equal(1d, Row(metrics, "startup.invalid_sig_count").Value.Number);
    }

    [Fact]
    public void Could_not_verify_is_never_counted_as_verified_and_is_a_note_not_a_failure()
    {
        var unknown = new AuthenticodeVerifier.Result(State.Unknown, Status: unchecked((int)0x800B010A));
        var (metrics, notes, failures) = Report(Item("Alpha", State.SignedAndValid), Item("Mystery", State.Unknown, unknown));

        var unsigned = Row(metrics, "startup.unsigned_count");
        Assert.Equal(0d, unsigned.Value.Number);
        Assert.DoesNotContain("valid publisher signature", unsigned.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be verified", unsigned.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be verified", Row(metrics, "startup.invalid_sig_count").Detail, StringComparison.Ordinal);

        // Named in its own note, with Windows' answer, and it does not degrade the scan: Windows
        // gave a verdict this check does not grade; nothing broke.
        var note = Assert.Single(notes);
        Assert.Contains("Mystery", note, StringComparison.Ordinal);
        Assert.Contains("0x800B010A", note, StringComparison.Ordinal);
        Assert.False(failures.Any);
    }

    [Fact]
    public void A_verifier_that_could_not_run_degrades_the_scan()
    {
        var broken = new AuthenticodeVerifier.Result(State.Unknown, Failure: "the catalog database could not be opened (error 0x80070005)");
        var (_, notes, failures) = Report(Item("Alpha", State.SignedAndValid), Item("Beta", State.Unknown, broken));

        Assert.Equal(1, failures.Count);
        Assert.Contains(notes, n => n.Contains("catalog database could not be opened", StringComparison.Ordinal));
    }

    [Fact]
    public void When_no_signature_could_be_checked_there_is_no_zero_to_read_as_clean()
    {
        var unknown = new AuthenticodeVerifier.Result(State.Unknown, Status: unchecked((int)0x800B0109));
        var (metrics, notes, _) = Report(Item("Alpha", State.Unknown, unknown), Item("Beta", State.Unknown, unknown));

        Assert.DoesNotContain(metrics, m => m.Id == "startup.unsigned_count");
        Assert.DoesNotContain(metrics, m => m.Id == "startup.invalid_sig_count");
        Assert.Contains(notes, n => n.Contains("left out rather than shown as zero", StringComparison.Ordinal));
        Assert.Equal(2d, Row(metrics, "startup.count").Value.Number);
    }

    [Fact]
    public void An_expired_certificate_without_a_timestamp_is_noted_not_called_broken()
    {
        // A timestamped signature whose certificate expired later verifies (LightShot, on the dev
        // T470p). Without a timestamp Windows no longer accepts it, but nothing says the file was
        // changed, so it is neither "BROKEN (possible tampering)" nor an all-clear.
        var (metrics, notes, failures) = Report(Item("Alpha", State.SignedAndValid), Item("OldTool", State.Expired));

        Assert.Equal(0d, Row(metrics, "startup.invalid_sig_count").Value.Number);
        Assert.Equal(0d, Row(metrics, "startup.unsigned_count").Value.Number);
        Assert.DoesNotContain("valid publisher signature", Row(metrics, "startup.unsigned_count").Detail, StringComparison.Ordinal);
        var note = Assert.Single(notes);
        Assert.Contains("OldTool", note, StringComparison.Ordinal);
        Assert.Contains("signed by Example Publisher LLC", note, StringComparison.Ordinal);
        Assert.False(failures.Any);
    }

    [Fact]
    public void Unsigned_items_are_still_listed_and_counted()
    {
        var (metrics, _, _) = Report(Item("Alpha", State.SignedAndValid), Item("Homemade", State.NotSigned));

        var unsigned = Row(metrics, "startup.unsigned_count");
        Assert.Equal(1d, unsigned.Value.Number);
        Assert.Contains(@"Homemade (D:\Tools\Homemade.exe)", unsigned.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_that_resolved_to_no_file_is_said_alongside_the_all_clear()
    {
        var (metrics, notes, _) = Report(Item("Alpha", State.SignedAndValid), Item("Gone", State.Unknown, resolved: false));

        var detail = Row(metrics, "startup.unsigned_count").Detail;
        Assert.StartsWith(AllValid, detail, StringComparison.Ordinal);
        Assert.Contains("could not be resolved to a file", detail, StringComparison.Ordinal);
        Assert.Single(notes);
    }

    [Fact]
    public void Disabled_items_are_not_counted()
    {
        var (metrics, _, _) = Report(Item("Alpha", State.SignedAndValid), Item("Off", State.Unknown, disabled: true));

        Assert.Equal(1d, Row(metrics, "startup.count").Value.Number);
        Assert.Equal(AllValid, Row(metrics, "startup.unsigned_count").Detail);
    }

    [Fact]
    public void The_list_names_the_publisher_only_where_the_signature_verified()
    {
        var (metrics, _, _) = Report(Item("Alpha", State.SignedAndValid), Item("Tampered", State.Invalid));

        var detail = Row(metrics, "startup.count").Detail;
        Assert.Contains("Alpha (Example Publisher LLC)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Tampered (", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_in_the_report_never_show_the_users_own_folder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, "Programs", "Example", "Example.exe");

        var (metrics, _, _) = Report(Item("Example", State.NotSigned, path: path));

        var detail = Row(metrics, "startup.unsigned_count").Detail!;
        Assert.Contains(@"%LOCALAPPDATA%\Programs\Example\Example.exe", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(localAppData, detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- Task Manager's switches ----------------------------------------------------------------

    private static readonly byte[] Enabled = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Disabled = [0x03, 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x01];

    [Fact]
    public void A_startup_folder_shortcut_turned_off_in_Task_Manager_reads_disabled()
    {
        // Filed by file name WITH its extension. The old code read these switches and then
        // listed every Startup folder item as starting anyway.
        var approved = new StartupApproved([("HKLM", StartupApproved.StartupFolder, "Example.lnk", Disabled)]);

        Assert.True(approved.IsDisabled("HKLM", StartupApproved.StartupFolder, "Example.lnk"));
        Assert.False(approved.IsDisabled("HKCU", StartupApproved.StartupFolder, "Example.lnk"));
    }

    [Fact]
    public void A_switch_belongs_to_one_place_not_to_a_name()
    {
        var approved = new StartupApproved(
        [
            ("HKCU", StartupApproved.Run, "Example", Disabled),
            ("HKLM", StartupApproved.Run, "Example", Enabled),
        ]);

        Assert.True(approved.IsDisabled("HKCU", StartupApproved.Run, "Example"));
        Assert.False(approved.IsDisabled("HKLM", StartupApproved.Run, "Example"));
        Assert.False(approved.IsDisabled("HKLM", StartupApproved.Run32, "Example"));
    }

    [Theory]
    [InlineData(new byte[] { 0x02 }, false)]
    [InlineData(new byte[] { 0x04 }, false)]   // seen on the dev T470p, enabled
    [InlineData(new byte[] { 0x06 }, false)]
    [InlineData(new byte[] { 0x03 }, true)]
    [InlineData(new byte[] { 0x01 }, true)]    // seen on the dev T470p with a disable time
    [InlineData(new byte[] { 0x05 }, true)]    // the same, for a Startup folder shortcut
    [InlineData(new byte[] { }, false)]
    public void An_odd_first_byte_means_disabled(byte[] value, bool disabled)
    {
        Assert.Equal(disabled, StartupApproved.MeansDisabled(value));
    }

    // --- Paths as tokens ------------------------------------------------------------------------

    private static readonly (string, string)[] Folders =
    [
        (@"C:\Users\Example\AppData\Local", "%LOCALAPPDATA%"),
        (@"C:\Users\Example\AppData\Roaming", "%APPDATA%"),
        (@"C:\Users\Example", "%USERPROFILE%"),
        (@"C:\Program Files (x86)", "%ProgramFiles(x86)%"),
        (@"C:\Program Files", "%ProgramFiles%"),
        (@"C:\Windows", "%SystemRoot%"),
    ];

    [Theory]
    [InlineData(@"C:\Users\Example\AppData\Local\Programs\Tool\Tool.exe", @"%LOCALAPPDATA%\Programs\Tool\Tool.exe")]
    [InlineData(@"c:\users\example\Desktop\Tool.exe", @"%USERPROFILE%\Desktop\Tool.exe")]
    [InlineData(@"C:\Program Files (x86)\Vendor\Tool.exe", @"%ProgramFiles(x86)%\Vendor\Tool.exe")]
    [InlineData(@"C:\Program Files\Vendor\Tool.exe", @"%ProgramFiles%\Vendor\Tool.exe")]
    [InlineData(@"C:\Windows\system32\Tool.exe", @"%SystemRoot%\system32\Tool.exe")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Tool.exe", @"<another account>\AppData\Local\Tool.exe")]
    [InlineData(@"C:\Users\ExampleTwo\Tool.exe", @"<another account>\Tool.exe")]   // not "%USERPROFILE%Two"
    [InlineData(@"D:\Tools\Tool.exe", @"D:\Tools\Tool.exe")]
    public void A_path_is_written_with_its_folder_as_a_token(string path, string expected)
    {
        Assert.Equal(expected, ReportPaths.Tokenize(path, Folders, @"C:\Users"));
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\userinit.exe,C:\Users\Example\AppData\Roaming\x.exe",
                @"%SystemRoot%\system32\userinit.exe,%APPDATA%\x.exe")]
    [InlineData(@"explorer.exe, C:\Users\Someone\x.exe", @"explorer.exe, <another account>\x.exe")]
    [InlineData(@"""C:\Users\Example\AppData\Local\x.exe"" /run", @"""%LOCALAPPDATA%\x.exe"" /run")]
    [InlineData(@"cmd.exe /c C:\Users\Example\x.bat", @"cmd.exe /c %USERPROFILE%\x.bat")]
    [InlineData(@"C:\Windows\system32\cmd.exe /c C:\Users\Example\x.bat", @"%SystemRoot%\system32\cmd.exe /c %USERPROFILE%\x.bat")]
    [InlineData("explorer.exe", "explorer.exe")]
    public void Every_path_in_a_command_or_a_list_is_written_with_its_folder_as_a_token(string text, string expected)
    {
        Assert.Equal(expected, ReportPaths.TokenizeWithin(text, Folders, @"C:\Users"));
    }

    /// <summary>
    /// The Winlogon row quoted Shell and Userinit as found, so a value planted under the user's
    /// folder put the user name into the very report most likely to be sent to IT (PRIV-002,
    /// review of 2026-10-28). Userinit is a comma-separated list, so a prefix match is not enough.
    /// </summary>
    [Fact]
    public void A_non_default_winlogon_value_is_written_without_the_users_folder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var planted = Path.Combine(appData, "Example", "x.exe");

        var row = StartupReport.Winlogon("explorer.exe", $@"{Environment.SystemDirectory}\userinit.exe,{planted},");

        Assert.False(row.Value.Flag);
        Assert.Contains(@"%APPDATA%\Example\x.exe", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(appData, row.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_winlogon_values_still_read_standard()
    {
        var row = StartupReport.Winlogon("explorer.exe", $@"{Environment.SystemDirectory}\userinit.exe,");

        Assert.True(row.Value.Flag);
        Assert.Equal("Shell and Userinit have their default values.", row.Detail);
        Assert.True(StartupReport.Winlogon(null, null).Value.Flag);   // absent under a key that opened: Windows' defaults
    }

    // --- Helpers --------------------------------------------------------------------------------

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static TempFile TempCopy(string? source, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "pchealth-sig-" + Guid.NewGuid().ToString("N")[..8] + extension);
        if (source is not null) File.Copy(source, path);
        return new TempFile(path);
    }

    /// <summary>Changes one byte a quarter of the way in: inside the program's code, well before
    /// the signature block at the end, so the file's digest no longer matches.</summary>
    private static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 4] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }
}
