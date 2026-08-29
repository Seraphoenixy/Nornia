using Nornia.Git.Parsing;
using Nornia.Core.Models;

namespace Nornia.Tests;

public sealed class GitLogParserTests
{
    [Fact]
    public void Parse_ExtractsCommitFields()
    {
        var output = string.Join("\x1e",
        [
            string.Join('\x1f', ["a".PadRight(40, '0'), "aaaaaaa", "feat: add git page", "Alice", "alice@example.com", "2025-01-02T10:20:30+08:00", "Body line one.\nBody line two."]),
            string.Join('\x1f', ["b".PadRight(40, '0'), "bbbbbbb", "fix: parser", "Bob", "bob@example.com", "2025-01-01T09:00:00Z", ""]),
            ""
        ]);

        var commits = GitLogParser.Parse(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal("aaaaaaa", commits[0].ShortHash);
        Assert.Equal("feat: add git page", commits[0].Subject);
        Assert.Equal("alice@example.com", commits[0].AuthorEmail);
        Assert.Equal("Body line one.\nBody line two.", commits[0].Body);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 10, 20, 30, TimeSpan.FromHours(8)), commits[0].AuthorDate);
        Assert.Equal(string.Empty, commits[1].Body);
    }

    [Fact]
    public void Parse_PreservesRawMessageLineBreaksWhenGitSubjectFlattensAParagraph()
    {
        var rawMessage = "feat: improve git\n- render markdown\n- allow selection";
        var output = string.Join('\x1f',
        [
            "a".PadRight(40, '0'), "aaaaaaa",
            "feat: improve git - render markdown - allow selection",
            "Alice", "alice@example.com", "2025-01-02T10:20:30+08:00",
            "", "", "", rawMessage
        ]) + "\x1e";

        var commit = Assert.Single(GitLogParser.Parse(output));

        Assert.Equal(rawMessage, commit.Message);
    }

    [Fact]
    public void Parse_EmptyOutputReturnsEmptyList()
    {
        Assert.Empty(GitLogParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_ExtractsParentHashes()
    {
        var output = string.Join('\x1f',
        [
            "a".PadRight(40, '0'), "aaaaaaa", "merge", "Alice", "alice@example.com",
            "2025-01-02T10:20:30+08:00", "", "b".PadRight(40, '0') + " " + "c".PadRight(40, '0')
        ]) + "\x1e";

        var commits = GitLogParser.Parse(output);

        Assert.Equal(["b".PadRight(40, '0'), "c".PadRight(40, '0')], commits.Single().ParentList);
    }

    [Fact]
    public void Parse_ExtractsRefsAndShortStats()
    {
        var first = string.Join('\x1f',
        [
            "a".PadRight(40, '0'), "aaaaaaa", "release", "Alice", "alice@example.com",
            "2025-01-02T10:20:30+08:00", "", "b".PadRight(40, '0'),
            "HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1.2.0"
        ]);
        var second = string.Join('\x1f',
        [
            "b".PadRight(40, '0'), "bbbbbbb", "previous", "Bob", "bob@example.com",
            "2025-01-01T09:00:00Z", "", "", ""
        ]);
        var output = first + "\x1e\n2 files changed, 3 insertions(+), 1 deletion(-)\x1e\n" +
                     second + "\x1e\n1 file changed, 2 deletions(-)\x1e";

        var commit = GitLogParser.Parse(output).First();

        Assert.Equal(
            [
                new GitRefInfo("main", GitRefKind.LocalBranch, true),
                new GitRefInfo("origin/main", GitRefKind.RemoteBranch),
                new GitRefInfo("v1.2.0", GitRefKind.Tag)
            ],
            commit.RefList);
        Assert.Equal(new GitCommitStats(2, 3, 1), commit.Stats);
    }

    [Fact]
    public void Parse_NoParentFieldYieldsEmptyParents()
    {
        var output = string.Join('\x1f',
        [
            "a".PadRight(40, '0'), "aaaaaaa", "root", "Alice", "alice@example.com",
            "2025-01-02T10:20:30+08:00", ""
        ]) + "\x1e";

        var commits = GitLogParser.Parse(output);

        Assert.Empty(commits.Single().ParentList);
    }

    [Fact]
    public void Parse_StripsGitAppendedNewlineBetweenRecords()
    {
        // git log 在每条格式化记录后追加换行,输出形如 r0\x1e\nr1\x1e\n…;若不剥离,
        // 第 2 条起 hash 带前导 '\n'(长度 41),父哈希永远匹配不上(每提交都像新分支点)。
        var chain = new[]
        {
            ("c2".PadRight(40, '0'), "c1".PadRight(40, '0')),
            ("c1".PadRight(40, '0'), "c0".PadRight(40, '0')),
            ("c0".PadRight(40, '0'), (string?)null),
        };
        var output = string.Concat(chain.Select((entry, i) =>
        {
            var record = string.Join('\x1f',
            [
                entry.Item1, entry.Item1[..7], $"subject-{i}", "Alice", "alice@example.com",
                "2025-01-02T10:20:30+08:00", "", entry.Item2 ?? ""
            ]);
            return record + "\x1e\n";
        }));

        var commits = GitLogParser.Parse(output);

        Assert.Equal(3, commits.Count);
        Assert.Equal(40, commits[1].Hash.Length);
        Assert.Equal("c1".PadRight(40, '0'), commits[0].ParentList.Single());
        Assert.Equal(commits[1].Hash, commits[0].ParentList.Single()); // 父哈希与下一提交哈希精确相等
        Assert.Equal(commits[2].Hash, commits[1].ParentList.Single());
        Assert.Empty(commits[2].ParentList);
    }
}
