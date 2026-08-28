using Nornia.Core.Models;
using System.Text.Json;

namespace Nornia.Package.Parsing;

/// <summary>
/// Contract-first parser for winget's structured output protocol. Stable winget builds (v1.10
/// observed) do not ship <c>--output json</c> on search/list yet; the schema below mirrors the
/// <c>winget export</c> JSON shape so the parser is ready to activate when a build reports
/// structured-output support. It stays isolated behind synthesized fixtures until real samples
/// confirm the contract; drifted schemas produce an empty result instead of throwing.
/// </summary>
public sealed class WingetJsonParser : IWingetOutputParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string Kind => "json";

    public WingetParseResult Parse(string output, bool isInstalled)
    {
        try
        {
            var document = JsonSerializer.Deserialize<WingetExport>(output, Options);
            if (document?.Sources is null || document.Sources.Count == 0)
            {
                return new WingetParseResult([], 0, 0, "json");
            }

            var packages = new List<PackageInfo>();
            var skipped = 0;
            foreach (var package in document.Sources.SelectMany(source => source.Packages ?? []))
            {
                if (string.IsNullOrWhiteSpace(package.PackageIdentifier) || string.IsNullOrWhiteSpace(package.PackageVersion))
                {
                    skipped++;
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(package.Name) ? package.PackageIdentifier : package.Name;
                packages.Add(new PackageInfo(
                    package.PackageIdentifier,
                    displayName,
                    package.PackageVersion,
                    string.IsNullOrWhiteSpace(package.AvailableVersion) ? null : package.AvailableVersion,
                    string.IsNullOrWhiteSpace(package.Source) ? WingetSources.Local : package.Source,
                    isInstalled,
                    WingetArchitecture.Detect(displayName, package.PackageIdentifier)));
            }

            return new WingetParseResult(packages, packages.Count, skipped, "json");
        }
        catch (JsonException)
        {
            return new WingetParseResult([], 0, 0, "json");
        }
    }

    private sealed class WingetExport
    {
        public List<WingetSource>? Sources { get; set; }
    }

    private sealed class WingetSource
    {
        public string? Name { get; set; }

        public List<WingetPackage>? Packages { get; set; }
    }

    private sealed class WingetPackage
    {
        public string? PackageIdentifier { get; set; }

        public string? PackageVersion { get; set; }

        public string? AvailableVersion { get; set; }

        public string? Name { get; set; }

        public string? Source { get; set; }
    }
}