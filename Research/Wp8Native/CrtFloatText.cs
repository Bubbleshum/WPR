using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Floating point to decimal text the way the VC11 CRT does it: printf's %e %f %g, and
    /// <c>_ecvt</c> / <c>_fcvt</c> / <c>_gcvt</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of those goes through two CRT steps, reproduced here because they decide the
    /// digits a game sees. <c>_fltout2</c> turns the double into <b>17 significant decimal digits</b>
    /// plus a decimal-point position; <c>_fptostr</c> then rounds that digit string half-up to the
    /// number of digits wanted. So digits past the seventeenth are always zero
    /// (<c>printf("%.20f", 0.1)</c> is <c>0.10000000000000001000</c> under VC11, not the exact
    /// <c>0.10000000000000000555</c>), and rounding happens on those decimal digits, not on the
    /// binary value (so <c>%.2f</c> of 0.125 is <c>0.13</c>).
    /// </para>
    /// <para>
    /// The exponent of %e and %g has <b>at least three digits</b> (<c>1.000000e+006</c>): that is
    /// VC11's default output format (two digits only after <c>_set_output_format</c>, which the
    /// CRT did not change until VS2015).
    /// </para>
    /// <para>
    /// Infinity and NaN are written as VC11 writes them, as a "digit string" of <c>1#INF</c>,
    /// <c>1#QNAN</c>, <c>1#SNAN</c> or <c>1#IND</c> (the indefinite NaN, sign bit set, which is what
    /// 0/0 produces): <c>%f</c> of infinity is <c>1.#INF00</c>, <c>%g</c> is <c>1.#INF</c>. VC11 also
    /// *rounds* that string when the precision is shorter than it (the infamous <c>1.#J</c>); here
    /// it is truncated instead, which is the one place this departs from the CRT.
    /// </para>
    /// </remarks>
    internal static class CrtFloatText
    {
        /// <summary>The CRT's _CVTBUFSIZE: the most digits _ecvt / _fcvt will produce, plus two.</summary>
        public const int CvtBufferSize = 349;

        /// <summary>What <c>_fltout2</c> hands back: sign, 17 digits (or a special), decimal point.</summary>
        public readonly record struct Parts(bool Negative, string Digits, int Point, bool Special);

        public static Parts Decompose(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;

            if (double.IsInfinity(value))
            {
                return new Parts(negative, "1#INF", 1, true);
            }

            if (double.IsNaN(value))
            {
                long mantissa = bits & 0xFFFFFFFFFFFFFL;
                string text = negative && mantissa == 0x8000000000000L ? "1#IND"
                    : (mantissa & 0x8000000000000L) != 0 ? "1#QNAN"
                    : "1#SNAN";
                return new Parts(negative, text, 1, true);
            }

            if (value == 0)
            {
                // $I10_OUTPUT reports zero as the digit "0" with exponent 0, so the point is at 1:
                // %e prints e+000 and %f a single leading zero.
                return new Parts(negative, new string('0', 17), 1, false);
            }

            // "E16" is the value correctly rounded to 17 significant digits, which is what the
            // CRT's 80-bit conversion produces for every double.
            string scientific = Math.Abs(value).ToString("E16", CultureInfo.InvariantCulture);
            int e = scientific.IndexOf('E');
            string digits = scientific[0] + scientific[2..e];
            int exponent = int.Parse(scientific[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return new Parts(negative, digits, exponent + 1, false);
        }

        /// <summary>
        /// <c>_fptostr</c>: the first <paramref name="count"/> digits, rounded half-up on the next
        /// one. A carry out of the top digit makes the result one digit longer and moves the point
        /// right. A negative count produces nothing and does not round.
        /// </summary>
        public static string Round(string mantissa, int count, ref int point)
        {
            if (count < 0)
            {
                return string.Empty;
            }

            char[] buffer = new char[count + 1];
            buffer[0] = '0';
            for (int i = 0; i < count; i++)
            {
                buffer[i + 1] = i < mantissa.Length ? mantissa[i] : '0';
            }

            char next = count < mantissa.Length ? mantissa[count] : '0';
            if (next >= '5')
            {
                int i = count;
                while (buffer[i] == '9')
                {
                    buffer[i--] = '0';
                }

                buffer[i]++;
            }

            if (buffer[0] == '1')
            {
                point++;
                return new string(buffer);
            }

            return new string(buffer, 1, count);
        }

        /// <summary>A special's digit string, cut or zero-padded to <paramref name="count"/>.</summary>
        private static string Fit(string digits, int count)
            => count <= 0 ? string.Empty : digits.Length >= count ? digits[..count] : digits.PadRight(count, '0');

        /// <summary>printf <c>%f</c> (with its sign).</summary>
        public static string Fixed(double value, int precision, bool forcePoint)
        {
            Parts parts = Decompose(value);
            int point = parts.Point;
            string digits = parts.Special
                ? Fit(parts.Digits, point + precision)
                : Round(parts.Digits, point + precision, ref point);
            return (parts.Negative ? "-" : string.Empty) + BuildFixed(digits, point, precision, forcePoint);
        }

        /// <summary>printf <c>%e</c> / <c>%E</c> (with its sign).</summary>
        public static string Exponential(double value, int precision, bool upper, bool forcePoint)
        {
            Parts parts = Decompose(value);
            int point = parts.Point;
            string digits = RoundSignificant(parts, precision + 1, ref point);
            return (parts.Negative ? "-" : string.Empty) + BuildExponential(digits, point, precision, upper, forcePoint);
        }

        /// <summary>
        /// printf <c>%g</c> / <c>%G</c>: rounded to <paramref name="precision"/> significant digits
        /// (0 counts as 1), then %e if the exponent is below -4 or at least the precision, %f
        /// otherwise; trailing fraction zeros and a bare point are removed unless # was given.
        /// </summary>
        public static string General(double value, int precision, bool upper, bool alternate)
        {
            int significant = precision == 0 ? 1 : precision;
            Parts parts = Decompose(value);
            int point = parts.Point;
            string digits = RoundSignificant(parts, significant, ref point);
            int magnitude = point - 1;

            string text = magnitude < -4 || magnitude >= significant
                ? BuildExponential(digits, point, significant - 1, upper, alternate)
                : BuildFixed(digits, point, significant - point, alternate);

            if (!alternate)
            {
                text = CropZeros(text, keepPoint: false);
            }

            return (parts.Negative ? "-" : string.Empty) + text;
        }

        /// <summary>
        /// <c>_gcvt</c>: <paramref name="count"/> significant digits in fixed notation when the
        /// exponent is from -1 to count-1, exponential otherwise. Trailing fraction zeros are
        /// removed but the decimal point is always kept (<c>_gcvt(-123456789012.3, 12)</c> is
        /// <c>"-123456789012."</c>, MSDN's own example).
        /// </summary>
        public static string Gcvt(double value, int count)
        {
            Parts parts = Decompose(value);
            int magnitude = parts.Point - 1;
            string text;
            if (magnitude < -1 || magnitude > count - 1)
            {
                int point = parts.Point;
                string digits = RoundSignificant(parts, count, ref point);
                text = BuildExponential(digits, point, count - 1, upper: false, forcePoint: false);
            }
            else
            {
                int point = parts.Point;
                int decimals = count - parts.Point;
                string digits = parts.Special ? Fit(parts.Digits, point + decimals) : Round(parts.Digits, point + decimals, ref point);
                text = BuildFixed(digits, point, decimals, forcePoint: true);
            }

            return (parts.Negative ? "-" : string.Empty) + CropZeros(text, keepPoint: true);
        }

        /// <summary><c>_ecvt</c>: <paramref name="count"/> significant digits, no point, no sign.</summary>
        public static (string Digits, int Point, bool Negative) Ecvt(double value, int count)
        {
            count = Math.Clamp(count, 0, CvtBufferSize - 2);
            Parts parts = Decompose(value);
            int point = parts.Point;
            return (RoundSignificant(parts, count, ref point), point, parts.Negative);
        }

        /// <summary><c>_fcvt</c>: every integer digit plus <paramref name="count"/> after the point.</summary>
        public static (string Digits, int Point, bool Negative) Fcvt(double value, int count)
        {
            Parts parts = Decompose(value);
            int point = parts.Point;
            int total = Math.Min(point + Math.Max(count, 0), CvtBufferSize - 2);
            string digits = parts.Special ? Fit(parts.Digits, total) : Round(parts.Digits, total, ref point);
            return (digits, point, parts.Negative);
        }

        /// <summary>Exactly <paramref name="count"/> significant digits; a carry moves the point instead of adding one.</summary>
        private static string RoundSignificant(Parts parts, int count, ref int point)
        {
            if (parts.Special)
            {
                return Fit(parts.Digits, count);
            }

            string digits = Round(parts.Digits, count, ref point);
            return digits.Length > count ? digits[..count] : digits;
        }

        private static string BuildFixed(string digits, int point, int decimals, bool forcePoint)
        {
            decimals = Math.Max(decimals, 0);
            string whole, fraction;
            if (point <= 0)
            {
                whole = "0";
                fraction = new string('0', Math.Min(-point, decimals)) + digits;
            }
            else
            {
                string padded = digits.Length < point ? digits.PadRight(point, '0') : digits;
                whole = padded[..point];
                fraction = padded[point..];
            }

            fraction = fraction.Length >= decimals ? fraction[..decimals] : fraction.PadRight(decimals, '0');
            return decimals > 0 || forcePoint ? whole + "." + fraction : whole;
        }

        private static string BuildExponential(string digits, int point, int decimals, bool upper, bool forcePoint)
        {
            decimals = Math.Max(decimals, 0);
            string padded = digits.PadRight(decimals + 1, '0');
            StringBuilder text = new();
            text.Append(padded[0]);
            if (decimals > 0 || forcePoint)
            {
                text.Append('.').Append(padded, 1, decimals);
            }

            int exponent = point - 1;   // a special's point is 1, so its exponent is +000
            text.Append(upper ? 'E' : 'e').Append(exponent < 0 ? '-' : '+')
                .Append(Math.Abs(exponent).ToString("D3", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>Removes trailing zeros from the fraction, and the point too unless it is to be kept.</summary>
        private static string CropZeros(string text, bool keepPoint)
        {
            int point = text.IndexOf('.');
            if (point < 0)
            {
                return text;
            }

            int exponent = text.IndexOfAny(['e', 'E'], point);
            string mantissa = exponent < 0 ? text : text[..exponent];
            string suffix = exponent < 0 ? string.Empty : text[exponent..];
            mantissa = mantissa.TrimEnd('0');
            if (!keepPoint && mantissa.EndsWith('.'))
            {
                mantissa = mantissa[..^1];
            }

            return mantissa + suffix;
        }
    }
}
