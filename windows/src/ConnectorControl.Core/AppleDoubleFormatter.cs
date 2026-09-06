using System.Globalization;

namespace ConnectorControl.Core;

internal static class AppleDoubleFormatter
{
    private const double TwoPow53 = 9007199254740992.0;

    public static string Format(double value, JsonNumberStyle style)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException("JSON cannot represent NaN or infinity.");
        }
        return style switch
        {
            JsonNumberStyle.Shortest => Shortest(value),
            JsonNumberStyle.G17 => value.ToString("G17", CultureInfo.InvariantCulture).Replace('E', 'e'),
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    /// <summary>
    /// Swift's Double.description as JSONEncoder prints it: shortest round-trip
    /// digits; exponent form when the value is below 1e-4 or above 2^53;
    /// no trailing ".0"; exponent has a sign and at least two digits.
    /// </summary>
    private static string Shortest(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }
        string sign = value < 0 ? "-" : "";
        // "R" gives the shortest digits that round-trip, e.g. "1E-05", "0.0001", "9.007199254740994E+15".
        string r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        string mantissa = r;
        int exp10 = 0;
        int e = r.IndexOf('E');
        if (e >= 0)
        {
            mantissa = r[..e];
            exp10 = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        int dot = mantissa.IndexOf('.');
        string digits = dot >= 0 ? mantissa.Remove(dot, 1) : mantissa;
        // Position of the decimal point counted from the start of `digits`.
        int pointPos = (dot >= 0 ? dot : mantissa.Length) + exp10;
        int leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        pointPos -= leadingZeros;
        digits = digits.TrimEnd('0');
        int sciExp = pointPos - 1;   // value = d.ddd × 10^sciExp

        bool exponential = sciExp < -4 || Math.Abs(value) > TwoPow53;
        if (exponential)
        {
            string m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            return sign + m + "e" + (sciExp < 0 ? "-" : "+") + Math.Abs(sciExp).ToString("00", CultureInfo.InvariantCulture);
        }
        if (pointPos <= 0)
        {
            return sign + "0." + new string('0', -pointPos) + digits;
        }
        if (pointPos >= digits.Length)
        {
            return sign + digits + new string('0', pointPos - digits.Length);
        }
        return sign + digits[..pointPos] + "." + digits[pointPos..];
    }
}
