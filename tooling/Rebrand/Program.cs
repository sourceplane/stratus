using System.Text;
using System.Text.RegularExpressions;

// Turns the baseline into a product: Stratus.* becomes Acme.*, in file
// CONTENTS and in the paths themselves.
//
// Why a .NET tool rather than a shell script: the thing being renamed is a
// .NET solution, and the only check that a rename actually worked is
// `dotnet build`. Keeping the tool in the language of the thing it edits means
// the verification and the tool share a runtime.
//
// ── What is deliberately NOT renamed ──
//
// Org-owned identity survives a fork. `sourceplane` (the GitHub org and the
// `sourceplane.io` apiVersion), `ghcr.io/sourceplane` and the pinned
// `stack-basalt` catalog reference all belong to the platform, not to the
// product — a fork that renamed them would point its intent at a composition
// stack nobody publishes. `flows/` is baseline machinery for the same reason:
// its umbrella fetches itself from `sourceplane/stratus` at the pinned tag,
// and a fork must keep fetching the BASELINE's flows, not its own.

var args_ = Environment.GetCommandLineArgs().Skip(1).ToArray();

string? Get(string name)
{
    var i = Array.IndexOf(args_, name);
    return i >= 0 && i + 1 < args_.Length ? args_[i + 1] : null;
}

var root = Get("--root") ?? ".";
var pascal = Get("--pascal");
var slug = Get("--slug");
var dryRun = args_.Contains("--dry-run");

// --values is how the Blueprint hook supplies the identity. Hook argv is NOT
// templated by `orun new` — `{{ inputs.pascalName }}` arrives as those literal
// characters — so the rendered values file is the channel. The explicit flags
// stay for running this by hand.
var valuesPath = Get("--values");
if (valuesPath is not null)
{
    if (!File.Exists(valuesPath))
    {
        Console.Error.WriteLine($"rebrand: no values file at {valuesPath}");
        return 1;
    }
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(valuesPath));
    string? FromValues(string key) =>
        doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
    pascal ??= FromValues("pascalName");
    slug ??= FromValues("repoName");
}

if (pascal is null || slug is null)
{
    Console.Error.WriteLine(
        "usage: Rebrand (--pascal <AcmeCloud> --slug <acme-cloud> | --values <path>) [--root <path>] [--dry-run]");
    return 2;
}

// The identifier has to be a legal C# namespace segment, because it becomes
// one in every file. Checking here turns a compile error in someone else's
// tree into a message at the point the value was supplied.
if (!Regex.IsMatch(pascal, "^[A-Z][A-Za-z0-9]*$"))
{
    Console.Error.WriteLine($"rebrand: --pascal '{pascal}' is not a valid C# identifier (^[A-Z][A-Za-z0-9]*$)");
    return 1;
}
if (!Regex.IsMatch(slug, "^[a-z][a-z0-9-]*$"))
{
    Console.Error.WriteLine($"rebrand: --slug '{slug}' is not a valid repo slug (^[a-z][a-z0-9-]*$)");
    return 1;
}

// Directories that hold build output, tool caches or baseline machinery.
// `flows` is in this list on purpose — see the header.
var skipDirs = new HashSet<string>(StringComparer.Ordinal)
{
    ".git", "bin", "obj", "artifacts", ".terraform", "node_modules", "TestResults", ".vs", "flows",
};

// Extensions worth rewriting. An allowlist rather than a denylist: a rename
// tool that guesses at unknown binary formats corrupts them silently.
var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".cs", ".csproj", ".slnx", ".props", ".targets", ".json", ".yaml", ".yml",
    ".md", ".editorconfig", ".sh", ".tf", ".template", ".Dockerfile", "",
};

static bool IsText(string path, HashSet<string> allowed)
{
    var ext = Path.GetExtension(path);
    if (ext.Length == 0)
    {
        // Extensionless files we do rewrite: Dockerfile and friends.
        var name = Path.GetFileName(path);
        return name is "Dockerfile" or ".gitignore" or ".editorconfig";
    }
    return allowed.Contains(ext);
}

var rootFull = Path.GetFullPath(root);

IEnumerable<string> Walk(string dir)
{
    foreach (var sub in Directory.EnumerateDirectories(dir))
    {
        if (skipDirs.Contains(Path.GetFileName(sub))) continue;
        foreach (var f in Walk(sub)) yield return f;
    }
    foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
}

/// <summary>
/// Rewrites the baseline's identifiers, leaving org-owned ones alone.
///
/// The lowercase pass is the delicate one: "stratus" appears both as the
/// product slug (rename it) and inside "sourceplane/stratus", the baseline's
/// own repository path (leave it). A negative lookbehind for the org is what
/// separates them, and it is why this is a regex rather than string.Replace.
/// </summary>
string Rewrite(string text)
{
    var swapped = text.Replace("Stratus", pascal, StringComparison.Ordinal);

    // snake_case identifiers first, and they need their own pass for two
    // reasons. `\bstratus\b` does not match inside `stratus_identity_design`,
    // because `_` is a word character and there is no boundary there — that
    // left the design-time database names carrying the baseline's identity
    // into every fork. And a hyphenated slug is not a legal bare identifier in
    // Postgres, so the underscore form is substituted here rather than the
    // slug as typed.
    var snake = slug.Replace('-', '_');
    swapped = Regex.Replace(swapped, @"(?<!sourceplane/)\bstratus_", snake + "_");

    swapped = Regex.Replace(swapped, @"(?<!sourceplane/)\bstratus\b", slug);
    return swapped;
}

var changedFiles = 0;
var renamed = 0;

foreach (var file in Walk(rootFull).ToList())
{
    if (!IsText(file, textExtensions)) continue;

    string original;
    try
    {
        original = File.ReadAllText(file);
    }
    catch (IOException)
    {
        continue;
    }

    var updated = Rewrite(original);
    if (!string.Equals(original, updated, StringComparison.Ordinal))
    {
        changedFiles++;
        if (!dryRun) File.WriteAllText(file, updated, new UTF8Encoding(false));
    }
}

// Paths last, and deepest-first: renaming a parent directory before its
// children invalidates every child path still to be visited.
var paths = Walk(rootFull)
    .Concat(Directory.EnumerateDirectories(rootFull, "*", SearchOption.AllDirectories)
        .Where(d => !d.Split(Path.DirectorySeparatorChar).Any(skipDirs.Contains)))
    .Where(p => Path.GetFileName(p).Contains("Stratus", StringComparison.Ordinal))
    .OrderByDescending(p => p.Count(c => c == Path.DirectorySeparatorChar))
    .ToList();

foreach (var path in paths)
{
    var dir = Path.GetDirectoryName(path)!;
    var name = Path.GetFileName(path).Replace("Stratus", pascal, StringComparison.Ordinal);
    var target = Path.Combine(dir, name);
    if (string.Equals(path, target, StringComparison.Ordinal)) continue;

    renamed++;
    if (dryRun) continue;

    if (Directory.Exists(path)) Directory.Move(path, target);
    else if (File.Exists(path)) File.Move(path, target);
}

Console.WriteLine(
    $"rebrand: {(dryRun ? "would rewrite" : "rewrote")} {changedFiles} file(s), "
    + $"{(dryRun ? "would rename" : "renamed")} {renamed} path(s) — Stratus → {pascal}, stratus → {slug}");

// A rebrand that renamed nothing is a rebrand that ran against the wrong tree,
// and reporting success there is how a fork gets shipped with the baseline's
// identity still in it.
if (changedFiles == 0 && renamed == 0)
{
    Console.Error.WriteLine($"rebrand: nothing matched under {rootFull} — wrong --root?");
    return 1;
}

return 0;
