using System.IO.Enumeration;
using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>Guards <see cref="SimpleGlob"/> (the precompiled replacement for per-call
/// <see cref="FileSystemName.MatchesSimpleExpression"/>) against semantic drift: the precompiled
/// matcher must agree with the framework on the exact same dialect, including its quirks
/// (\* = literal star, \? keeps the wildcard, a trailing \ matches zero or one arbitrary
/// character, leading-* fast path is a plain case-insensitive suffix test). The product adds
/// the conventional recursive **/ path-segment semantics used by the search UI; those vectors
/// are covered by WorkspaceSearchServiceTests instead of this framework-equivalence suite.</summary>
public sealed class SimpleGlobEquivalenceTests
{
    [Fact]
    public void CompiledMatcher_AgreesWithFrameworkOnProbeVectors()
    {
        var cases = new (string Pattern, string Name)[]
        {
            // Wildcards
            ("*", "a"), ("*", "a/b"), ("*", ""),
            ("**", "a/b/c"), ("**", ""),
            ("a*b", "ab"), ("a*b", "a/b"), ("a*b", "aX/Yb"), ("a*b", "xaYb"),
            ("a?b", "a/b"), ("a?b", "a.b"), ("a?b", "axb"), ("a?b", "a  b"),
            ("*", "/"), ("?", "/"), ("?", "a/b"),
            // Escapes
            ("a\\*b", "a*b"), ("a\\*b", "aXb"), ("a\\*b", "a/b"),
            ("a\\?b", "a?b"), ("a\\?b", "axb"), ("a\\?b", "a/b"),
            ("a\\\\b", "a\\b"), ("a\\\\b", "ab"), ("a\\\\b", "aXb"),
            ("a\\b", "ab"), ("a\\b", "a\\b"),
            // Trailing backslash quirk (zero or one arbitrary character)
            ("a\\", "a"), ("a\\", "ab"), ("a\\", "abc"), ("a\\", "a/"),
            ("\\", "x"), ("\\", "xy"),
            ("a?\\", "ab"), ("a?\\", "abc"), ("a?\\", "abcd"), ("a?\\", "a"),
            // Literal backslash via \\
            ("a\\\\", "a\\"), ("a\\\\", "a\\x"), ("a\\\\", "a"),
            // Literal characters incl. brackets/dot
            ("[a", "[a"), ("[a", "a"), ("*.txt", "a.txt"), ("*.txt", "txt"),
            ("*.txt", "a/b.TXT"), ("src/*", "src/a"), ("src/*", "src"),
            // Leading-star fast path
            ("*b", "ab"), ("*b", "a/b"),
            ("*a\\", "xa\\"), ("*a\\", "xa"),
            ("*\\*", "a\\*b"), ("*\\*", "aX*b"),
        };

        foreach (var (pattern, name) in cases)
        {
            if (pattern.Contains("**", StringComparison.Ordinal)) continue;
            var expected = FileSystemName.MatchesSimpleExpression(pattern, name, true);
            var actual = SimpleGlob.Compile(pattern)(name);
            Assert.True(actual == expected,
                $"pattern='{pattern.Replace("\\", "<bs>")}' name='{name.Replace("\\", "<bs>")}' framework={expected} compiled={actual}");
        }
    }

    [Fact]
    public void CompiledMatcher_AgreesWithFrameworkOnRandomizedInputs()
    {
        var random = new Random(0x5EED);
        const string patternAlphabet = "ab/*?\\.";
        const string nameAlphabet = "ab/*?\\.";

        var failures = 0;
        for (var iteration = 0; iteration < 40_000; iteration++)
        {
            var pattern = RandomString(random, patternAlphabet, 0, 6);
            var name = RandomString(random, nameAlphabet, 0, 5);
            if (pattern.Contains("**", StringComparison.Ordinal)) continue;
            var expected = FileSystemName.MatchesSimpleExpression(pattern, name, true);
            var actual = SimpleGlob.Compile(pattern)(name);
            if (expected != actual)
            {
                failures++;
                if (failures <= 5)
                {
                    Assert.Fail(
                        $"pattern='{pattern.Replace("\\", "<bs>")}' name='{name.Replace("\\", "<bs>")}' framework={expected} compiled={actual}");
                }
            }
        }

        Assert.Equal(0, failures);
    }

    private static string RandomString(Random random, string alphabet, int min, int max)
    {
        var length = min + random.Next(max - min + 1);
        var characters = new char[length];
        for (var i = 0; i < length; i++) characters[i] = alphabet[random.Next(alphabet.Length)];
        return new(characters);
    }
}
