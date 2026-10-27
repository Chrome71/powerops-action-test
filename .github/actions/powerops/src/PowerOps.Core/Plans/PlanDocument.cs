using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerOps.Core.Plans;

/// <summary>The parsed header of a plan.md. Values are already validated.</summary>
public sealed record PlanHeader(string Status, string Review, DateOnly? SignedOff, string? SignedOffHash, string? Environment, string? Depth, string? Supersedes);

/// <summary>A plan.md split into header and body. Not YAML: plain <c>key: value</c> lines between two <c>---</c> lines.</summary>
public sealed partial class PlanDocument
{
    private static readonly string[] Statuses = ["draft", "approved", "signed-off", "implementing", "done"];
    private static readonly string[] Reviews = ["pending", "done", "skipped"];
    private static readonly string[] Keys = ["status", "review", "signed-off", "signed-off-hash", "environment", "depth", "supersedes"];
    private static readonly string[] Depths = ["quick", "standard", "deep"];

    /// <summary>Null when the header is malformed.</summary>
    public PlanHeader? Header { get; private init; }

    /// <summary>Why the header is malformed, or null.</summary>
    public string? Error { get; private init; }

    /// <summary>SHA-256 (lowercase hex) of the body with LF line endings; null when a fence is missing. Computed even if a header value is invalid.</summary>
    public string? BodyHash { get; private init; }

    public int OpenQuestions { get; private init; }

    // Raw lines with their terminators, and the index of the closing fence (-1 when there is none).
    internal IReadOnlyList<string> Lines { get; private init; } = [];

    internal int Close { get; private init; } = -1;

    public static PlanDocument Parse(string text)
    {
        if (text.StartsWith('﻿'))
        {
            text = text[1..];
        }

        var lines = LineRegex().Matches(text).Select(m => m.Value).ToList();
        var open = CountOpenQuestions(lines);
        if (lines.Count == 0 || !IsFence(lines[0]))
        {
            return new PlanDocument { Error = "the file does not start with a '---' line", OpenQuestions = open, Lines = lines };
        }

        var close = lines.FindIndex(1, IsFence);
        if (close < 0)
        {
            return new PlanDocument { Error = "the header has no closing '---' line", OpenQuestions = open, Lines = lines };
        }

        var body = string.Concat(lines.Skip(close + 1)).Replace("\r\n", "\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var (header, error) = ParseHeader(lines, close);
        return new PlanDocument { Header = header, Error = error, BodyHash = hash, OpenQuestions = open, Lines = lines, Close = close };
    }

    [GeneratedRegex("[^\n]*\n|[^\n]+")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"(^|\s)#.*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"^(- |\* |\d+\. )")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex("^#{1,2} ")]
    private static partial Regex HeadingRegex();

    // No dots, slashes or colons, so it can never leave docs/plans/ (C:foo is rooted on Windows), and no tenant data can be echoed.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")]
    private static partial Regex PlanNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex WordRegex();

    // Only plain words are echoed back, so a pasted URL or other tenant data never reaches a CI log.
    internal static bool Word(string s) => WordRegex().IsMatch(s);

    internal static string Quoted(string s) => Word(s) ? $" '{s}'" : "";

    private static bool IsFence(string line) => line.TrimEnd() == "---";

    private static string Content(string line) => line.TrimEnd('\r', '\n');

    // Headings that start a counted section end at the next level 1 or 2 heading.
    private static int CountOpenQuestions(List<string> lines)
    {
        var count = 0;
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = Content(raw);
            if (HeadingRegex().IsMatch(line))
            {
                var title = line.Trim();
                inSection = title.Equals("## Open questions", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("## Questions for your admin", StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && ListItemRegex().IsMatch(line))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Splits a header line into key and value (comment removed, trimmed, empty or "null" becomes null). Null key when the line has no ':'.</summary>
    internal static (string? Key, string? Value) SplitLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return (null, null);
        }

        var value = CommentRegex().Replace(line[(colon + 1)..], "").Trim();
        return (line[..colon].Trim(), value is "" or "null" ? null : value);
    }

    private static (PlanHeader?, string?) ParseHeader(List<string> lines, int close)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 1; i < close; i++)
        {
            var content = Content(lines[i]);
            if (content.Trim().Length == 0)
            {
                continue;
            }

            var (key, value) = SplitLine(content);
            if (key is null)
            {
                return (null, $"line {i + 1} has no ':'");
            }

            if (!Keys.Contains(key))
            {
                return (null, Word(key) ? $"unknown key '{key}'" : $"line {i + 1} has an unknown key");
            }

            if (!values.TryAdd(key, value))
            {
                return (null, $"duplicate key '{key}'");
            }
        }

        string? Get(string key) => values.GetValueOrDefault(key);

        var status = Get("status");
        if (status is null)
        {
            return (null, "status is missing");
        }

        if (!Statuses.Contains(status))
        {
            return (null, $"status{Quoted(status)} is not one of {string.Join(", ", Statuses)}");
        }

        var review = Get("review");
        if (review is null)
        {
            return (null, "review is missing");
        }

        if (!Reviews.Contains(review))
        {
            return (null, $"review{Quoted(review)} is not one of {string.Join(", ", Reviews)}");
        }

        DateOnly? signedOff = null;
        if (Get("signed-off") is { } date)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return (null, $"signed-off{Quoted(date)} is not a date (yyyy-MM-dd)");
            }

            signedOff = parsed;
        }

        var hash = Get("signed-off-hash");
        if (hash is not null && !HashRegex().IsMatch(hash))
        {
            return (null, "signed-off-hash is not 64 lowercase hex characters");
        }

        var depth = Get("depth");
        if (depth is not null && !Depths.Contains(depth))
        {
            return (null, $"depth{Quoted(depth)} is not one of {string.Join(", ", Depths)}");
        }

        var supersedes = Get("supersedes");
        if (supersedes is not null && !PlanNameRegex().IsMatch(supersedes))
        {
            return (null, "supersedes must be a plan folder name (letters, digits, '-' and '_'), not a path");
        }

        return (new PlanHeader(status, review, signedOff, hash, Get("environment"), depth, supersedes), null);
    }
}