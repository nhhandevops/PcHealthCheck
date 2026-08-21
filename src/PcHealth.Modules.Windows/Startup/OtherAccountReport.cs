using System.Globalization;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>What was found for one account, and whether the search was complete.</summary>
public sealed record AccountStartupResult(string AccountName, IReadOnlyList<string> Items, bool FullyExamined);

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
    public static string DescribeAccounts(IReadOnlyList<AccountStartupResult> results)
    {
        if (results.Count == 0) return "";

        return string.Join(" | ", results.Select(result =>
        {
            var found = result.Items.Count > 0
                ? string.Join("; ", result.Items.Take(12)) + (result.Items.Count > 12 ? " …" : "")
                : result.FullyExamined
                    ? "nothing starts up for this account"
                    : "nothing found in the part that could be read";

            var caveat = result.FullyExamined ? "" : " (NOT fully examined — see the note below)";
            return $"{result.AccountName}: {found}{caveat}.";
        }));
    }

    /// <summary>
    /// The sentence beside the total. A zero is only allowed to mean "nothing is there" when
    /// every account was read in full; otherwise it means "nothing in the part we saw", and
    /// says so.
    /// </summary>
    public static string DescribeTotal(int totalItems, int partiallyExamined, int accounts)
    {
        var headline = totalItems > 0
            ? "These run when that person signs in, not when you do. Anything here you do not recognise is worth " +
              "asking about — a program that only starts for one account is a favourite hiding place."
            : partiallyExamined == 0
                ? "Nothing is set to start for any other account on this PC."
                : "Nothing was found in the parts that could be read.";

        if (partiallyExamined == 0) return headline;

        return headline +
               $" This counts only what could be read: {Format(partiallyExamined)} of {Format(accounts)} account(s) " +
               "could not be examined in full, so a zero here does not mean nothing is there.";
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
