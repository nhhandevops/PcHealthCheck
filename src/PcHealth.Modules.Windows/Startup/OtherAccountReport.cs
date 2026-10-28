using System.Globalization;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>What was found for one account, and whether the search was complete.</summary>
/// <param name="Account">The account's number as the report shows it ("Account 2"), never its name.</param>
public sealed record AccountStartupResult(string Account, IReadOnlyList<string> Items, bool FullyExamined);

/// <summary>
/// Turns per-account results into the sentences the report shows.
///
/// Pure and separate because of the bug it exists to prevent, which reached a real screen:
/// the check reported "Programs starting for other accounts: 0 — Nothing is set to start for
/// any other account on this PC" while, three lines above, saying it had not been able to
/// read that account's registry at all. The number and the sentence beside it must agree
/// about what was actually looked at.
///
/// This is the same failure as a threat count of zero from a query that never ran, and the
/// third time it has appeared in this codebase. The wording only takes its dangerous branch
/// when coverage is incomplete — which is exactly the state a healthy development machine is
/// never in, so it needs tests rather than a careful reading.
/// </summary>
public static class OtherAccountReport
{
    /// <summary>One clause per account: what was found, and whether the account was fully read.</summary>
    /// <param name="startedFrom">The number of the account the scan was started from, said first so
    /// the reader can place the others: accounts are numbered, never named. Null when that account
    /// has no profile on this PC.</param>
    /// <param name="readersOwn">Whether that account is known to be the reader's. An unelevated window
    /// runs as the person signed in, so a scan it started is theirs. An app started with "Run as
    /// administrator" may be running as whoever typed the password, so its account is named only as
    /// the one that started the scan.</param>
    public static string DescribeAccounts(IReadOnlyList<AccountStartupResult> results, string? startedFrom = null, bool readersOwn = true)
    {
        if (results.Count == 0) return "";

        var prefix = startedFrom is null ? ""
            : readersOwn ? $"Yours is {startedFrom}. "
            : $"The account that started this scan is {startedFrom}. ";
        return prefix + string.Join(" | ", results.Select(result =>
        {
            var found = result.Items.Count > 0
                ? string.Join("; ", result.Items.Take(12)) + (result.Items.Count > 12 ? " …" : "")
                : result.FullyExamined
                    ? "nothing starts up for this account"
                    : "nothing found in the part that could be read";

            var caveat = result.FullyExamined ? "" : " (NOT fully examined — see the note below)";
            return $"{result.Account}: {found}{caveat}.";
        }));
    }

    /// <summary>
    /// The sentence beside the total. A zero is only allowed to mean "nothing is there" when
    /// every account was read in full; otherwise it means "nothing in the part we saw", and
    /// says so.
    /// </summary>
    /// <param name="readersOwn">As for <see cref="DescribeAccounts"/>.</param>
    public static string DescribeTotal(int totalItems, int partiallyExamined, int accounts, bool readersOwn = true)
    {
        var headline = totalItems > 0
            ? (readersOwn
                  ? "These run when that person signs in, not when you do. "
                  : "These run when that account signs in, not when the account that started this scan does. ") +
              "Anything here you do not recognise is worth asking about — a program that only starts for one " +
              "account is a favourite hiding place."
            : partiallyExamined == 0
                ? "Nothing is set to start for any other account on this PC."
                : "Nothing was found in the parts that could be read.";

        if (partiallyExamined == 0) return headline;

        return headline +
               $" This counts only what could be read: {Format(partiallyExamined)} of {Format(accounts)} account(s) " +
               "could not be examined in full, so a zero here does not mean nothing is there.";
    }

    /// <summary>The sentence when the account the scan was started from is the only one here.</summary>
    public static string OnlyAccount(bool readersOwn) =>
        (readersOwn ? "Yours is" : "The account that started this scan is") +
        " the only user account on this PC, so the ordinary startup check already covers everything that starts here.";

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
