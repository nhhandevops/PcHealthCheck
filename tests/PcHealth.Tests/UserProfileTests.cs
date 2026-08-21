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

    [Fact]
    public void This_machines_own_profile_list_yields_at_least_the_current_account()
    {
        // Runs against the real registry: whatever else is here, the account running the
        // tests must appear, and must be the one flagged as current.
        var notes = new List<string>();
        var profiles = UserProfiles.Enumerate(notes);

        var current = Assert.Single(profiles, p => p.IsCurrentUser);
        Assert.False(string.IsNullOrWhiteSpace(current.AccountName));
        Assert.True(Path.IsPathRooted(current.ProfilePath));
        Assert.All(profiles, p => Assert.True(UserProfiles.IsRealUserAccount(p.Sid)));
    }
}
