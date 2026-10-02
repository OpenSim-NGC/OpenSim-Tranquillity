using System;

namespace InWorldz.Phlox.Util
{
    /// <summary>
    /// Float to integer as LSL does it. SL wiki (Typecast): "Typecasting from a float to an integer merely
    /// removes the portion of the number following the decimal point"; (llFloor): "The returned value is -2147483648
    /// (0x80000000) if the arithmetic result is outside of the range of valid integers (-2147483648 to 2147483647
    /// inclusive)". NaN gives -2147483648 too, as SL and Halcyon (.NET Framework x64 cvttss2si) do. A plain C# cast
    /// no longer does this: from .NET 9 on x64 it saturates (NaN 0, +inf 2147483647).
    /// </summary>
    public static class LslConvert
    {
        public static int FloatToInteger(double f)
        {
            if (double.IsNaN(f) || f >= 2147483648.0 || f <= -2147483649.0)
                return int.MinValue;
            return (int)f;
        }
    }
}
