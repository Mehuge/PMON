namespace PMON.Data;

public interface IPointSource
{
    int Count { get; }
    DateTime TimeAt(int index);
    double ValueAt(int index);
    int FirstAtOrAfter(DateTime time);
    int LastAtOrBefore(DateTime time);
}

public sealed class PointSource<T>(IReadOnlyList<T> samples, Func<T, double> selector) : IPointSource where T : ITimedSample
{
    public int Count => samples.Count;
    public DateTime TimeAt(int index) => samples[index].Time;
    public double ValueAt(int index) => selector(samples[index]);
    public int FirstAtOrAfter(DateTime time) => TimeSearch.FirstAtOrAfter(samples, time);
    public int LastAtOrBefore(DateTime time) => TimeSearch.LastAtOrBefore(samples, time);
}
