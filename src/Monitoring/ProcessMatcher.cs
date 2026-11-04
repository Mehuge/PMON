using System.Text.RegularExpressions;
using PMON.Models;

namespace PMON.Monitoring;

/// <summary>Matches process names against wildcard patterns ("chrome*", "*sql?") or regexes ("re:^code( helper)?$"); exclude rules veto includes.</summary>
public sealed class ProcessMatcher
{
    private const string RegexPrefix = "re:";
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public ProcessMatcher(IEnumerable<TrackingRule> rules)
    {
        var active = rules.Where(r => r.Enabled).ToList();
        _include = CompileAll(active.Where(r => !r.Exclude));
        _exclude = CompileAll(active.Where(r => r.Exclude));
    }

    private ProcessMatcher(Regex expression)
    {
        _include = [expression];
        _exclude = [];
    }

    public static ProcessMatcher None { get; } = new([]);

    /// <summary>A matcher for one pattern on its own, or null when the pattern is empty or invalid.</summary>
    public static ProcessMatcher? ForPattern(string pattern) =>
        Compile(pattern) is { } expression ? new ProcessMatcher(expression) : null;

    public bool IsMatch(string processName)
    {
        if (_include.Length == 0)
            return false;

        var withExtension = processName + ".exe";
        return Any(_include, processName, withExtension) && !Any(_exclude, processName, withExtension);
    }

    private static bool Any(Regex[] expressions, string name, string withExtension)
    {
        foreach (var expression in expressions)
        {
            if (expression.IsMatch(name) || expression.IsMatch(withExtension))
                return true;
        }

        return false;
    }

    private static Regex[] CompileAll(IEnumerable<TrackingRule> rules) =>
        rules.Select(r => Compile(r.Pattern)).OfType<Regex>().ToArray();

    private static Regex? Compile(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return null;

        var source = pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase)
            ? pattern[RegexPrefix.Length..]
            : "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        try
        {
            return new Regex(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
