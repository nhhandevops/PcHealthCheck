using System.Globalization;
using PcHealth.Core.Advice;

namespace PcHealth.Core.Text;

/// <summary>
/// Writes an argument the same way on every PC, whatever its regional format: a Vietnamese or German
/// Windows must not turn 8.3 GB into "8,3 GB" (REP-002, LNX-001). Numbers are rounded here, away
/// from zero, and then written with the invariant culture; never with <c>$"{x:0.0}"</c>, which
/// follows the current culture and which CA1305 does not flag.
/// </summary>
public static class AdviceFormat
{
    /// <summary>The argument as it is printed, or null when it has no usable value: neither or both of Number and Text, or a number that is not finite.</summary>
    public static string? Format(AdviceArg arg) => (arg.Number, arg.Text) switch
    {
        ({ } number, null) when double.IsFinite(number) && arg.Unit != AdviceUnit.Date => FormatNumber(number, arg.Unit),
        (null, { } text) => text,
        _ => null,
    };

    /// <summary>
    /// Percentages keep one decimal when they have one: free space is graded Bad under 10%, and
    /// 9.9% written as "10%" beside "under 10% free" contradicts its own grade. The readings are
    /// rounded to 0.1 where they are made, so this prints the value that was graded.
    /// </summary>
    private static string FormatNumber(double number, AdviceUnit unit) => unit switch
    {
        AdviceUnit.Count or AdviceUnit.Days => Whole(number),
        AdviceUnit.Percent => Round(number, 1).ToString("0.#", CultureInfo.InvariantCulture) + "%",
        AdviceUnit.Gb => (Math.Abs(Round(number, 1)) >= 10 ? Whole(number) : Round(number, 1).ToString("0.0", CultureInfo.InvariantCulture)) + " GB",
        AdviceUnit.Minutes => Whole(AdviceThresholds.RoundUpMinutes(number)),
        AdviceUnit.Celsius => Whole(number) + " °C",
        _ => Round(number, 2).ToString("0.##", CultureInfo.InvariantCulture),
    };

    private static string Whole(double number) => Round(number, 0).ToString("0", CultureInfo.InvariantCulture);

    private static double Round(double number, int digits) => Math.Round(number, digits, MidpointRounding.AwayFromZero);
}
