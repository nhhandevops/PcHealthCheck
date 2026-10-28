using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Startup;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Telling people apart from Windows' own service identities. Get this wrong in one
/// direction and the report lists half a dozen accounts nobody recognises, which is how a
/// real finding gets lost; get it wrong in the other and a genuine second account is never
/// examined at all.
/// </summary>
public class UserProfileTests
{
    [Theory]
    [InlineData("S-1-5-21-3024176233-81127509-442900678-1001")]   // the usual first account
    [InlineData("S-1-5-21-3024176233-81127509-442900678-1004")]   // a second account on the same PC
    [InlineData("S-1-5-21-1-2-3-500")]                            // the built-in Administrator
    public void Accounts_issued_to_people_are_examined(string sid)
    {
        Assert.True(UserProfiles.IsRealUserAccount(sid));
    }

    [Theory]
    [InlineData("S-1-5-18")]                                              // SYSTEM
    [InlineData("S-1-5-19")]                                              // LOCAL SERVICE
    [InlineData("S-1-5-20")]                                              // NETWORK SERVICE
    [InlineData("S-1-5-80-863171341-2975503981-1811344707-3769924460-3995132968")]  // a per-service SID
    [InlineData("S-1-5-82-1234567890-123456789")]                         // an IIS app pool
    [InlineData(".DEFAULT")]
    [InlineData("")]
    public void Windows_own_identities_are_left_out(string sid)
    {
        // All of these have profile folders on a normal PC. Listing them as "accounts" would
        // bury the one account that actually belongs to somebody.
        Assert.False(UserProfiles.IsRealUserAccount(sid));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-499")]   // below the first real account id
    [InlineData("S-1-5-21-1-2-3-")]      // truncated
    [InlineData("S-1-5-21-1-2-3-abc")]   // not a number
    [InlineData("nonsense")]
    public void Malformed_or_reserved_identifiers_are_refused_rather_than_guessed_at(string sid)
    {
        Assert.False(UserProfiles.IsRealUserAccount(sid));
    }

    [Fact]
    public void A_profile_this_account_may_not_read_is_reported_as_unreadable_not_as_empty()
    {
        // The trap, verified against a real second account on the development PC: when a
        // profile folder is denied, .NET reports paths *beneath* it as missing rather than
        // forbidden. Directory.Exists on its Startup folder returns false — so a check built
        // that way says "nothing starts up for this account" about an account it never saw.
        var notes = new List<string>();
        var others = UserProfiles.Enumerate(notes).Where(p => !p.IsCurrentUser).ToList();
        if (others.Count == 0) return;   // single-account PC: nothing to prove here

        foreach (var profile in others)
        {
            var startup = Path.Combine(profile.ProfilePath,
                "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

            if (UserProfiles.CanRead(profile.ProfilePath, out var failure))
            {
                Assert.Null(failure);
                continue;
            }

            // Unreadable: the probe must say so, and this is exactly the case where the
            // cheaper Directory.Exists test would have quietly answered "no such folder".
            Assert.False(string.IsNullOrWhiteSpace(failure));
            Assert.False(Directory.Exists(startup));
        }
    }

    [Fact]
    public void The_current_users_own_profile_reads_cleanly()
    {
        var mine = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(UserProfiles.CanRead(mine, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void A_path_that_is_not_there_at_all_is_also_reported_rather_than_thrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pchealth-no-such-profile-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.False(UserProfiles.CanRead(missing, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    /// <summary>
    /// The bug that reached a real screen: the check reported "Programs starting for other
    /// accounts: 0 — Nothing is set to start for any other account on this PC" while, three
    /// lines above, saying it had not been able to read that account's registry at all.
    /// A number and the sentence beside it must agree about what was actually looked at.
    /// </summary>
    [Fact]
    public void A_zero_from_a_partial_search_never_claims_nothing_is_there()
    {
        var text = OtherAccountReport.DescribeTotal(totalItems: 0, partiallyExamined: 1, accounts: 1);

        Assert.DoesNotContain("Nothing is set to start for any other account", text, StringComparison.Ordinal);
        Assert.Contains("only what could be read", text, StringComparison.Ordinal);
        Assert.Contains("a zero here does not mean nothing is there", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_from_a_complete_search_is_allowed_to_say_so()
    {
        // The opposite guard: an app that hedges even when it did look properly is an app
        // whose warnings stop meaning anything.
        var text = OtherAccountReport.DescribeTotal(totalItems: 0, partiallyExamined: 0, accounts: 2);

        Assert.Contains("Nothing is set to start for any other account", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not mean nothing is there", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_still_carry_the_coverage_warning()
    {
        var text = OtherAccountReport.DescribeTotal(totalItems: 3, partiallyExamined: 1, accounts: 2);

        Assert.Contains("signs in, not when you do", text, StringComparison.Ordinal);
        Assert.Contains("could not be examined in full", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_could_not_be_fully_read_is_marked_as_such_in_its_own_line()
    {
        var text = OtherAccountReport.DescribeAccounts(
        [
            new AccountStartupResult("Account 2", [], FullyExamined: true),
            new AccountStartupResult("Account 3", [], FullyExamined: false),
        ]);

        Assert.Contains("Account 2: nothing starts up for this account.", text, StringComparison.Ordinal);
        Assert.Contains("Account 3: nothing found in the part that could be read", text, StringComparison.Ordinal);
        Assert.Contains("NOT fully examined", text, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_found_is_listed_per_account()
    {
        var text = OtherAccountReport.DescribeAccounts(
            [new AccountStartupResult("Account 2", ["Updater [Run]"], FullyExamined: true)]);

        Assert.Contains("Updater [Run]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT fully examined", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_says_which_numbered_account_is_the_readers_own()
    {
        var text = OtherAccountReport.DescribeAccounts(
            [new AccountStartupResult("Account 2", [], FullyExamined: true)], startedFrom: "Account 1");

        Assert.StartsWith("Yours is Account 1. Account 2: nothing starts up", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: an app started with "Run as administrator" may be running as whoever
    /// typed the password, so the row cannot call that account the reader's (PRIV-002, review of
    /// 2026-10-28).
    /// </summary>
    [Fact]
    public void An_account_not_known_to_be_the_readers_is_named_only_as_the_one_that_started_the_scan()
    {
        var accounts = OtherAccountReport.DescribeAccounts(
            [new AccountStartupResult("Account 2", ["Updater [Run]"], FullyExamined: true)], startedFrom: "Account 1", readersOwn: false);
        var total = OtherAccountReport.DescribeTotal(totalItems: 1, partiallyExamined: 0, accounts: 1, readersOwn: false);

        Assert.StartsWith("The account that started this scan is Account 1. Account 2: Updater [Run].", accounts, StringComparison.Ordinal);
        Assert.DoesNotContain("Yours", accounts, StringComparison.Ordinal);
        Assert.DoesNotContain("not when you do", total, StringComparison.Ordinal);
        Assert.Contains("not when the account that started this scan does", total, StringComparison.Ordinal);
        Assert.StartsWith("Yours is", OtherAccountReport.OnlyAccount(readersOwn: true), StringComparison.Ordinal);
        Assert.DoesNotContain("Yours", OtherAccountReport.OnlyAccount(readersOwn: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper runs as whoever typed the administrator password. On a company laptop that is a
    /// technician, so "yours" and "other" were decided from the technician's account: the
    /// employee's own was listed as another person's, and the technician's Run keys were read by
    /// neither check (PRIV-002, review of 2026-10-28). The window now tells the helper which account
    /// started the scan.
    /// </summary>
    [Fact]
    public void The_account_the_scan_started_from_is_the_current_one_not_the_process_running_the_check()
    {
        var notes = new List<string>();
        var mine = Assert.Single(UserProfiles.Enumerate(notes), p => p.IsCurrentUser);

        // A made-up account standing for whoever started the scan: this process's own is then another one.
        var startedElsewhere = UserProfiles.Enumerate(notes, startedBySid: "S-1-5-21-1-2-3-1001");
        Assert.DoesNotContain(startedElsewhere, p => p.IsCurrentUser);
        Assert.Contains(startedElsewhere, p => p.Sid == mine.Sid);

        // And a real second account on this PC, where there is one.
        if (startedElsewhere.FirstOrDefault(p => p.Sid != mine.Sid) is { } other)
        {
            var asOther = UserProfiles.Enumerate(notes, startedBySid: other.Sid);
            Assert.Equal(other.Sid, Assert.Single(asOther, p => p.IsCurrentUser).Sid);
        }

        Assert.Equal(mine.Sid, Assert.Single(UserProfiles.Enumerate(notes, startedBySid: mine.Sid), p => p.IsCurrentUser).Sid);
    }

    [Fact]
    public async Task The_helper_counts_the_signed_in_account_as_yours_and_its_own_as_another()
    {
        var profiles = UserProfiles.Enumerate([]);
        var mine = Assert.Single(profiles, p => p.IsCurrentUser);

        // As the elevated helper when the window was started from another account: this process's
        // own account is now one of the others, read like any other.
        var elsewhere = await OtherAccountsRowAsync(startedBySid: "S-1-5-21-1-2-3-1001", isElevated: true);
        Assert.Equal((double)profiles.Count, elsewhere.Value.Number);
        Assert.DoesNotContain("Yours is " + mine.Label + ".", elsewhere.Detail ?? "", StringComparison.Ordinal);

        // As the helper started from this account, which the window knows is the person signed in.
        var helper = await OtherAccountsRowAsync(startedBySid: mine.Sid, isElevated: true);
        Assert.Equal((double)profiles.Count - 1, helper.Value.Number);
        Assert.StartsWith("Yours is ", helper.Detail, StringComparison.Ordinal);

        // As an app started with "Run as administrator": whose account this is cannot be known.
        var runAsAdmin = await OtherAccountsRowAsync(startedBySid: null, isElevated: true);
        Assert.Equal((double)profiles.Count - 1, runAsAdmin.Value.Number);
        Assert.StartsWith("The account that started this scan is ", runAsAdmin.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Yours", runAsAdmin.Detail, StringComparison.Ordinal);
    }

    /// <summary>Runs the real check against this PC's registry, as the helper or the elevated app would.</summary>
    private static async Task<Metric> OtherAccountsRowAsync(string? startedBySid, bool isElevated)
    {
        var receipt = new ReceiptLog();
        var context = new ModuleContext
        {
            Processes = new ProcessRunner(receipt),
            Receipt = receipt,
            AppTempDir = Path.GetTempPath(),
            IsElevated = isElevated,
            StartedBySid = startedBySid,
        };
        var module = new WindowsModuleProvider().CreateModules().Single(m => m.Manifest.Id == "startup.allusers");
        var result = await module.RunAsync(context, null, CancellationToken.None);
        return Assert.Single(result.Metrics, m => m.Id == "startup.other_accounts");
    }

    [Fact]
    public void This_machines_own_profile_list_yields_at_least_the_current_account()
    {
        // Runs against the real registry: whatever else is here, the account running the
        // tests must appear, and must be the one flagged as current.
        var notes = new List<string>();
        var profiles = UserProfiles.Enumerate(notes);

        var current = Assert.Single(profiles, p => p.IsCurrentUser);
        Assert.True(Path.IsPathRooted(current.ProfilePath));
        Assert.All(profiles, p => Assert.True(UserProfiles.IsRealUserAccount(p.Sid)));
        Assert.Equal(Enumerable.Range(1, profiles.Count), profiles.Select(p => p.Number));
    }

    /// <summary>
    /// Principle 10: a report names no user. This check used to write other people's account
    /// names, and the full commands their Run entries hold, into the report (PRIV-002). The
    /// numbers follow the order Windows issued the accounts, so "Account 2" is the same account
    /// whoever is signed in when the scan runs.
    /// </summary>
    [Fact]
    public void Accounts_are_numbered_in_the_order_Windows_issued_them_never_named()
    {
        var numbered = UserProfiles.Numbered(
        [
            new UserProfile("S-1-5-21-1-2-3-1004", @"C:\Users\Placeholder4"),
            new UserProfile("S-1-5-21-1-2-3-1001", @"C:\Users\Placeholder1") { IsCurrentUser = true },
            new UserProfile("S-1-5-21-1-2-3-500", @"C:\Users\Placeholder0"),
        ]);

        Assert.Equal(["Account 1", "Account 2", "Account 3"], numbered.Select(p => p.Label));
        Assert.Equal("S-1-5-21-1-2-3-500", numbered[0].Sid);
        Assert.Equal("S-1-5-21-1-2-3-1001", numbered[1].Sid);
        Assert.True(numbered[1].IsCurrentUser);
        Assert.All(numbered, p => Assert.DoesNotContain("Placeholder", p.Label, StringComparison.Ordinal));
    }
}
