using System.Globalization;

namespace PMON.Data;

public enum ValueKind
{
    Percent,
    Megabytes,
    BytesPerSecond,
    PerSecond,
    Count,
}

public static class ValueFormatter
{
    public static string Format(ValueKind kind, double value) => kind switch
    {
        ValueKind.Percent => $"{Trim(value, 1)}%",
        ValueKind.Megabytes => value >= 1024 ? $"{Trim(value / 1024, 2)} GB" : $"{Trim(value, 1)} MB",
        ValueKind.BytesPerSecond => value >= 1024 * 1024 ? $"{Trim(value / 1048576, 2)} MB/s"
            : value >= 1024 ? $"{Trim(value / 1024, 1)} KB/s" : $"{Trim(value, 0)} B/s",
        ValueKind.PerSecond => $"{Trim(value, 1)}/s",
        _ => Trim(value, 0),
    };

    private static string Trim(double value, int decimals) =>
        Math.Round(value, decimals).ToString(decimals == 0 ? "N0" : "0." + new string('#', decimals), CultureInfo.CurrentCulture);
}
