namespace Nornia.Project.Models;

public sealed class VersionConstraint
{
    private VersionConstraint(string text, VersionValue? prefix, IReadOnlyList<VersionComparator> comparators)
    {
        Text = text;
        Prefix = prefix;
        Comparators = comparators;
    }

    public string Text { get; }
    public VersionValue? Prefix { get; }
    public IReadOnlyList<VersionComparator> Comparators { get; }
    public bool AllowsPrerelease => Prefix?.Prerelease is not null || Comparators.Any(comparator => comparator.Version.Prerelease is not null);

    public bool IsSatisfiedBy(string installedVersion)
    {
        if (!VersionValue.TryParseInstalled(installedVersion, out var installed))
        {
            return false;
        }

        if (installed.Prerelease is not null && !AllowsPrerelease)
        {
            return false;
        }

        return Prefix is not null
            ? Prefix.IsPrefixOf(installed)
            : Comparators.All(comparator => comparator.IsSatisfiedBy(installed));
    }

    public string? GetRepairTargetVersion()
    {
        if (Prefix is not null)
        {
            return Prefix.ToString();
        }

        return Comparators.FirstOrDefault(comparator => comparator.Operator == VersionComparisonOperator.GreaterThanOrEqual)?.Version.ToString();
    }

    internal static VersionConstraint CreatePrefix(string text, VersionValue prefix) => new(text, prefix, []);
    internal static VersionConstraint CreateRange(string text, IReadOnlyList<VersionComparator> comparators) => new(text, null, comparators);
}

public static class VersionConstraintParser
{
    public static bool TryParse(string? text, out VersionConstraint? constraint)
    {
        constraint = null;
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.StartsWith('>') || normalized.StartsWith('<'))
        {
            var comparators = new List<VersionComparator>();
            foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!VersionComparator.TryParse(token, out var comparator))
                {
                    return false;
                }

                comparators.Add(comparator);
            }

            if (comparators.Count == 0)
            {
                return false;
            }

            constraint = VersionConstraint.CreateRange(normalized, comparators);
            return true;
        }

        if (!VersionValue.TryParse(normalized, out var prefix))
        {
            return false;
        }

        constraint = VersionConstraint.CreatePrefix(normalized, prefix);
        return true;
    }
}

public sealed record VersionValue(IReadOnlyList<int> Parts, string? Prerelease)
{
    public static bool TryParse(string value, out VersionValue version)
        => TryParseCore(value, allowRuntimeSuffix: false, out version);

    public static bool TryParseInstalled(string value, out VersionValue version)
        => TryParseCore(value, allowRuntimeSuffix: true, out version);

    private static bool TryParseCore(string value, bool allowRuntimeSuffix, out VersionValue version)
    {
        version = null!;
        var normalized = value.Trim().TrimStart('v');
        var separator = normalized.IndexOf('-');
        var numbers = separator < 0 ? normalized : normalized[..separator];
        var prerelease = separator < 0 ? null : normalized[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(numbers) || (separator >= 0 && string.IsNullOrWhiteSpace(prerelease)))
        {
            return false;
        }

        var parts = new List<int>();
        foreach (var part in numbers.Split('.'))
        {
            if (!int.TryParse(part, out var number) || number < 0)
            {
                if (allowRuntimeSuffix && parts.Count > 0)
                {
                    break;
                }

                return false;
            }

            parts.Add(number);
        }

        if (parts.Count == 0)
        {
            return false;
        }

        version = new VersionValue(parts, prerelease);
        return true;
    }

    public bool IsPrefixOf(VersionValue candidate) =>
        Parts.Count <= candidate.Parts.Count
        && Parts.SequenceEqual(candidate.Parts.Take(Parts.Count))
        && (Prerelease is not null || candidate.Prerelease is null);

    public int CompareTo(VersionValue other)
    {
        for (var index = 0; index < Math.Max(Parts.Count, other.Parts.Count); index++)
        {
            var comparison = (index < Parts.Count ? Parts[index] : 0).CompareTo(index < other.Parts.Count ? other.Parts[index] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (Prerelease is null && other.Prerelease is not null) return 1;
        if (Prerelease is not null && other.Prerelease is null) return -1;
        return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => string.Join('.', Parts) + (Prerelease is null ? string.Empty : $"-{Prerelease}");
}

public sealed record VersionComparator(VersionComparisonOperator Operator, VersionValue Version)
{
    public bool IsSatisfiedBy(VersionValue installed) => Operator switch
    {
        VersionComparisonOperator.GreaterThan => installed.CompareTo(Version) > 0,
        VersionComparisonOperator.GreaterThanOrEqual => installed.CompareTo(Version) >= 0,
        VersionComparisonOperator.LessThan => installed.CompareTo(Version) < 0,
        VersionComparisonOperator.LessThanOrEqual => installed.CompareTo(Version) <= 0,
        _ => false
    };

    public static bool TryParse(string token, out VersionComparator comparator)
    {
        comparator = null!;
        var (symbol, @operator) = token.StartsWith(">=", StringComparison.Ordinal) ? (">=", VersionComparisonOperator.GreaterThanOrEqual)
            : token.StartsWith("<=", StringComparison.Ordinal) ? ("<=", VersionComparisonOperator.LessThanOrEqual)
            : token.StartsWith('>') ? (">", VersionComparisonOperator.GreaterThan)
            : token.StartsWith('<') ? ("<", VersionComparisonOperator.LessThan)
            : (string.Empty, default);
        return !string.IsNullOrEmpty(symbol)
            && VersionValue.TryParse(token[symbol.Length..], out var version)
            && (comparator = new VersionComparator(@operator, version)) is not null;
    }
}

public enum VersionComparisonOperator
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}
