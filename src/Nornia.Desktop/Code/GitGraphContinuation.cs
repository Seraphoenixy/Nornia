namespace Nornia.Desktop.Code;

/// <summary>Lanes present below a commit, including the expanded file list.</summary>
public static class GitGraphContinuation
{
    public static IReadOnlyList<int> OutgoingLanes(
        IEnumerable<int> passLanes, IEnumerable<int> parentLanes) =>
        passLanes.Concat(parentLanes).Distinct().Order().ToArray();
}
