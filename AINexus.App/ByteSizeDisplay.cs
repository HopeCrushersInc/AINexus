using System.Globalization;

namespace AINexus.App;

/// <summary>Shows actual byte counts without rounding small nonzero files down to zero GiB.</summary>
public static class ByteSizeDisplay
{
    public static string Format(long? bytes)
    {
        if (bytes is null or < 0) return "Unknown";
        if (bytes < 1024) return bytes.Value.ToString("N0", CultureInfo.CurrentCulture) + " B";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string ModelSize(long? bytes) => bytes is > 0 ? Format(bytes) : "Unknown";
}
