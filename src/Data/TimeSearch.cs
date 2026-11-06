namespace PMON.Data;

public interface ITimedSample
{
    DateTime Time { get; }
}

public static class TimeSearch
{
    /// <summary>Index of the first sample at or after <paramref name="time"/>; Count if none.</summary>
    public static int FirstAtOrAfter<T>(IReadOnlyList<T> list, DateTime time) where T : ITimedSample
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (list[mid].Time < time)
            {
                low = mid + 1;
                continue;
            }

            high = mid;
        }

        return low;
    }

    /// <summary>Index of the last sample at or before <paramref name="time"/>; -1 if none.</summary>
    public static int LastAtOrBefore<T>(IReadOnlyList<T> list, DateTime time) where T : ITimedSample
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (list[mid].Time <= time)
            {
                low = mid + 1;
                continue;
            }

            high = mid;
        }

        return low - 1;
    }
}
