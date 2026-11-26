namespace Custos.Statistics;

/// <summary>
/// 統計の計算に使う特殊関数（対数ガンマ、不完全ベータ、不完全ガンマ、正規分布、t 分布）。
/// 外部のライブラリに頼らず、決まった入力に決まった結果を返す。
/// </summary>
public static class SpecialFunctions
{
    private const double Epsilon = 1e-15;
    private const double TinyNumber = 1e-300;
    private const int MaxIterations = 10_000;

    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993,
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    /// <summary>ガンマ関数の自然対数 ln Γ(x)（x &gt; 0）。Lanczos 近似。</summary>
    public static double LogGamma(double x)
    {
        if (x <= 0 || double.IsNaN(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "x は正の数にしてください");
        }

        if (x < 0.5)
        {
            // 反射公式 Γ(x)Γ(1−x) = π / sin(πx)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        }

        x -= 1;
        var a = LanczosCoefficients[0];
        var t = x + 7.5;
        for (var i = 1; i < LanczosCoefficients.Length; i++)
        {
            a += LanczosCoefficients[i] / (x + i);
        }

        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(a);
    }

    /// <summary>ベータ関数の自然対数 ln B(a, b)。</summary>
    public static double LogBeta(double a, double b) => LogGamma(a) + LogGamma(b) - LogGamma(a + b);

    /// <summary>
    /// 正則化不完全ベータ関数 I_x(a, b)。ベータ分布 Beta(a, b) の累積分布関数にあたる。
    /// 連分数（修正 Lentz 法）で計算する。
    /// </summary>
    public static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (a <= 0 || b <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "a と b は正の数にしてください");
        }

        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        var logFront = (a * Math.Log(x)) + (b * Math.Log(1 - x)) - LogBeta(a, b);
        if (x < (a + 1) / (a + b + 2))
        {
            return Math.Exp(logFront) * BetaContinuedFraction(x, a, b) / a;
        }

        return 1 - (Math.Exp(logFront) * BetaContinuedFraction(1 - x, b, a) / b);
    }

    private static double BetaContinuedFraction(double x, double a, double b)
    {
        var qab = a + b;
        var qap = a + 1;
        var qam = a - 1;
        var c = 1.0;
        var d = 1 - (qab * x / qap);
        if (Math.Abs(d) < TinyNumber)
        {
            d = TinyNumber;
        }

        d = 1 / d;
        var h = d;
        for (var m = 1; m <= MaxIterations; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + (aa * d);
            if (Math.Abs(d) < TinyNumber)
            {
                d = TinyNumber;
            }

            c = 1 + (aa / c);
            if (Math.Abs(c) < TinyNumber)
            {
                c = TinyNumber;
            }

            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + (aa * d);
            if (Math.Abs(d) < TinyNumber)
            {
                d = TinyNumber;
            }

            c = 1 + (aa / c);
            if (Math.Abs(c) < TinyNumber)
            {
                c = TinyNumber;
            }

            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon)
            {
                return h;
            }
        }

        return h;
    }

    /// <summary>
    /// ベータ分布 Beta(a, b) の分位点（I_x(a, b) = p となる x）。二分法で求める。
    /// </summary>
    public static double InverseRegularizedIncompleteBeta(double p, double a, double b)
    {
        if (p <= 0)
        {
            return 0;
        }

        if (p >= 1)
        {
            return 1;
        }

        double lo = 0;
        double hi = 1;
        for (var i = 0; i < 200; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (RegularizedIncompleteBeta(mid, a, b) < p)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }

            if (hi - lo < 1e-16)
            {
                break;
            }
        }

        return 0.5 * (lo + hi);
    }

    /// <summary>正則化下側不完全ガンマ関数 P(a, x)。</summary>
    public static double RegularizedLowerGamma(double a, double x)
    {
        if (a <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), a, "a は正の数にしてください");
        }

        if (x <= 0)
        {
            return 0;
        }

        if (x < a + 1)
        {
            // 級数展開
            var sum = 1 / a;
            var term = sum;
            var ap = a;
            for (var n = 1; n <= MaxIterations; n++)
            {
                ap += 1;
                term *= x / ap;
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * Epsilon)
                {
                    break;
                }
            }

            return sum * Math.Exp((-x) + (a * Math.Log(x)) - LogGamma(a));
        }

        return 1 - RegularizedUpperGammaContinuedFraction(a, x);
    }

    private static double RegularizedUpperGammaContinuedFraction(double a, double x)
    {
        var b = x + 1 - a;
        var c = 1 / TinyNumber;
        var d = 1 / b;
        var h = d;
        for (var i = 1; i <= MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2;
            d = (an * d) + b;
            if (Math.Abs(d) < TinyNumber)
            {
                d = TinyNumber;
            }

            c = b + (an / c);
            if (Math.Abs(c) < TinyNumber)
            {
                c = TinyNumber;
            }

            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon)
            {
                break;
            }
        }

        return Math.Exp((-x) + (a * Math.Log(x)) - LogGamma(a)) * h;
    }

    /// <summary>誤差関数 erf(x)。</summary>
    public static double Erf(double x)
    {
        if (x == 0)
        {
            return 0;
        }

        var value = RegularizedLowerGamma(0.5, x * x);
        return x > 0 ? value : -value;
    }

    /// <summary>標準正規分布の累積分布関数 Φ(x)。</summary>
    public static double NormalCdf(double x)
    {
        if (double.IsPositiveInfinity(x))
        {
            return 1;
        }

        if (double.IsNegativeInfinity(x))
        {
            return 0;
        }

        if (x < -8)
        {
            // 裾は上側不完全ガンマで直接計算する（桁落ちを避ける）
            return 0.5 * RegularizedUpperGammaContinuedFraction(0.5, x * x / 2);
        }

        if (x > 8)
        {
            return 1 - (0.5 * RegularizedUpperGammaContinuedFraction(0.5, x * x / 2));
        }

        return 0.5 * (1 + Erf(x / Math.Sqrt(2)));
    }

    /// <summary>標準正規分布の分位点 Φ⁻¹(p)。Acklam の近似を、Halley 法で1回補正する。</summary>
    public static double NormalQuantile(double p)
    {
        if (p <= 0 || p >= 1 || double.IsNaN(p))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p は 0 より大きく 1 より小さくしてください");
        }

        const double plow = 0.02425;
        const double phigh = 1 - plow;
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

        double x;
        if (p < plow)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((((((c[0] * q) + c[1]) * q) + c[2]) * q) + c[3]) * q) + c[4]) * q) + c[5];
            x /= (((((((d[0] * q) + d[1]) * q) + d[2]) * q) + d[3]) * q) + 1;
        }
        else if (p <= phigh)
        {
            var q = p - 0.5;
            var r = q * q;
            x = (((((((((a[0] * r) + a[1]) * r) + a[2]) * r) + a[3]) * r) + a[4]) * r) + a[5];
            x *= q;
            x /= (((((((((b[0] * r) + b[1]) * r) + b[2]) * r) + b[3]) * r) + b[4]) * r) + 1;
        }
        else
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -((((((((((c[0] * q) + c[1]) * q) + c[2]) * q) + c[3]) * q) + c[4]) * q) + c[5]);
            x /= (((((((d[0] * q) + d[1]) * q) + d[2]) * q) + d[3]) * q) + 1;
        }

        // Halley 法で1回補正
        var e = NormalCdf(x) - p;
        var u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        x -= u / (1 + (x * u / 2));
        return x;
    }

    /// <summary>自由度 df の t 分布の累積分布関数。</summary>
    public static double StudentTCdf(double t, double degreesOfFreedom)
    {
        if (degreesOfFreedom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom), degreesOfFreedom, "自由度は正の数にしてください");
        }

        if (double.IsPositiveInfinity(t))
        {
            return 1;
        }

        if (double.IsNegativeInfinity(t))
        {
            return 0;
        }

        var x = degreesOfFreedom / (degreesOfFreedom + (t * t));
        var tail = 0.5 * RegularizedIncompleteBeta(x, degreesOfFreedom / 2, 0.5);
        return t > 0 ? 1 - tail : tail;
    }

    /// <summary>自由度 df の t 分布の分位点。累積分布関数を二分法で解く。</summary>
    public static double StudentTQuantile(double p, double degreesOfFreedom)
    {
        if (p <= 0 || p >= 1 || double.IsNaN(p))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p は 0 より大きく 1 より小さくしてください");
        }

        if (p == 0.5)
        {
            return 0;
        }

        // 探索範囲を広げながら、根を挟む
        double lo = -1;
        double hi = 1;
        while (StudentTCdf(lo, degreesOfFreedom) > p)
        {
            lo *= 2;
            if (lo < -1e12)
            {
                return double.NegativeInfinity;
            }
        }

        while (StudentTCdf(hi, degreesOfFreedom) < p)
        {
            hi *= 2;
            if (hi > 1e12)
            {
                return double.PositiveInfinity;
            }
        }

        for (var i = 0; i < 300; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (StudentTCdf(mid, degreesOfFreedom) < p)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }

            if (hi - lo < 1e-13 * Math.Max(1, Math.Abs(mid)))
            {
                break;
            }
        }

        return 0.5 * (lo + hi);
    }
}
