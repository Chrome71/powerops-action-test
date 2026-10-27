using System.CommandLine;
using System.Text;

namespace PowerOps.Cli;

// Installs the planning skills embedded from the repo-root skills/ folder.
public static class SkillsCommand
{
    private const int UsageError = 2;
    private const string ReviewSkill = "powerops-review-plan/SKILL.md";
    private static readonly string[] Agents = ["claude", "codex", "gemini", "copilot"];

    public static Command Create(TextWriter output, TextWriter error)
    {
        var command = new Command("skills", "Install the PowerOps planning skills into a repo.");
        var path = new Argument<string>("path") { Description = "The repo folder to install into.", DefaultValueFactory = _ => "." };
        var agent = new Option<string[]>("--agent") { Description = "Agent to install for (claude, codex, gemini, copilot); repeatable. Default: detect from the repo.", AllowMultipleArgumentsPerToken = true };
        agent.AcceptOnlyFromAmong(Agents);
        var force = new Option<bool>("--force") { Description = "Overwrite skill files that differ from the shipped ones." };
        var install = new Command("install", "Copy the skills to .claude/skills/ and/or .agents/skills/. Never deletes files.") { path, agent, force };
        install.SetAction(parse => PlanCommand.Guard(error, () =>
        {
            var root = parse.GetValue(path)!;
            if (!Directory.Exists(root))
            {
                error.WriteLine($"error: path not found: {root}");
                return UsageError;
            }

            return Install(root, parse.GetValue(agent) ?? [], parse.GetValue(force), output, error);
        }));
        command.Subcommands.Add(install);
        return command;
    }

    /// <summary>The shipped files keyed by "<skill>/<relative path>", with LF line endings.</summary>
    public static IReadOnlyDictionary<string, string> Shipped()
    {
        var assembly = typeof(SkillsCommand).Assembly;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var logical = name.Replace('\\', '/');
            if (!logical.StartsWith("skills/", StringComparison.Ordinal))
            {
                continue;
            }

            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
            files[logical["skills/".Length..]] = reader.ReadToEnd().Replace("\r\n", "\n");
        }

        return files;
    }

    /// <summary>The Claude Code copy of a file: the review skill runs in a fresh context on the top-tier model alias.</summary>
    public static string ForClaude(string relativePath, string content)
    {
        if (relativePath != ReviewSkill)
        {
            return content;
        }

        var lines = content.Split('\n').ToList();
        var close = lines.FindIndex(1, l => l.TrimEnd() == "---");
        if (lines[0].TrimEnd() != "---" || close < 0)
        {
            throw new InvalidOperationException($"{relativePath} has no front matter");
        }

        lines.InsertRange(close, ["context: fork", "background: false", "model: opus"]);
        return string.Join('\n', lines);
    }

    private static int Install(string root, string[] agents, bool force, TextWriter output, TextWriter error)
    {
        var claude = agents.Length > 0 ? agents.Contains("claude") : Detect(root, [".claude"], ["CLAUDE.md"]);
        var shared = agents.Length > 0 ? agents.Any(a => a != "claude") : Detect(root, [".agents", ".codex", ".gemini"], ["AGENTS.md"]);
        if (agents.Length == 0 && !claude && !shared)
        {
            claude = shared = true;
        }

        var targets = new List<(string Dir, bool Claude)>();
        if (claude)
        {
            targets.Add((Path.Combine(root, ".claude", "skills"), true));
        }

        if (shared)
        {
            targets.Add((Path.Combine(root, ".agents", "skills"), false));
        }

        // Plan every file first, so a conflict writes nothing.
        var plan = new List<(string File, string Content, string State)>();
        foreach (var (dir, isClaude) in targets)
        {
            foreach (var (rel, text) in Shipped())
            {
                var content = isClaude ? ForClaude(rel, text) : text;
                var file = Path.Combine(dir, rel);
                var state = Directory.Exists(file) ? "directory"
                    : !File.Exists(file) ? "created"
                    : File.ReadAllText(file).Replace("\r\n", "\n") == content ? "unchanged"
                    : force ? "updated" : "conflict";
                plan.Add((file, content, state));
            }
        }

        var baseDir = Directory.GetCurrentDirectory();
        var directories = plan.Where(p => p.State == "directory").ToList();
        if (directories.Count > 0)
        {
            directories.ForEach(d => error.WriteLine($"error: {Display(d.File, baseDir)} is a directory"));
            return 1;
        }

        var conflicts = plan.Where(p => p.State == "conflict").ToList();
        if (conflicts.Count > 0)
        {
            conflicts.ForEach(c => error.WriteLine($"error: {Display(c.File, baseDir)} differs from the shipped skill; rerun with --force to overwrite"));
            return 1;
        }

        foreach (var (file, content, state) in plan)
        {
            if (state != "unchanged")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content, new UTF8Encoding(false));
            }

            output.WriteLine($"{state} {Display(file, baseDir)}");
        }

        return 0;
    }

    private static bool Detect(string root, string[] dirs, string[] files) =>
        dirs.Any(d => Directory.Exists(Path.Combine(root, d))) || files.Any(f => File.Exists(Path.Combine(root, f)));

    // Relative to the current directory when underneath it, otherwise the full path; forward slashes.
    private static string Display(string path, string baseDir)
    {
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(baseDir, full);
        var under = !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return (under ? rel : full).Replace('\\', '/');
    }
}