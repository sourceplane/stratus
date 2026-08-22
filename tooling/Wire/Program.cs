using System.Text.Json;
using System.Text.RegularExpressions;

// Renders a committed config template into a deployable config, replacing
// @@wiring(<component>/<env>:<key>)@@ tokens.
//
// Two sources, deliberately one tool:
//   --fixture <path>   offline, from a committed fixture (verify lanes)
//   --components/--environments   from the environment, where the infra
//                      components published their outputs as env vars
//
// A token that resolves to nothing is a hard failure. The alternative — an
// empty string in a connection setting — produces a service that deploys
// cleanly and cannot reach anything it needs, diagnosed an hour later.

var args_ = Environment.GetCommandLineArgs().Skip(1).ToArray();

string? Get(string name)
{
    var i = Array.IndexOf(args_, name);
    return i >= 0 && i + 1 < args_.Length ? args_[i + 1] : null;
}

var templatePath = Get("--template");
var outputPath = Get("--output");
var fixturePath = Get("--fixture");
var environment = Get("--environment") ?? "stage";

if (templatePath is null || outputPath is null)
{
    Console.Error.WriteLine("usage: Wire --template <path> --output <path> [--fixture <path>] --environment <env>");
    return 2;
}

if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"wire: template not found: {templatePath}");
    return 1;
}

// Fixture shape: { "<component>": { "<env>": { "<key>": "<value>" } } }
Dictionary<string, Dictionary<string, Dictionary<string, string>>> fixture = new();
if (fixturePath is not null && File.Exists(fixturePath))
{
    var json = File.ReadAllText(fixturePath);
    fixture = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(json)
              ?? new();
}

var template = File.ReadAllText(templatePath);
var pattern = new Regex(@"@@wiring\(([^/]+)/([^:]+):([^)]+)\)@@");
var unresolved = new List<string>();

var rendered = pattern.Replace(template, match =>
{
    var component = match.Groups[1].Value;
    var env = match.Groups[2].Value;
    var key = match.Groups[3].Value;

    // Fixture first when one was supplied (the offline lane), otherwise the
    // environment, where the infra components' published outputs land.
    if (fixture.TryGetValue(component, out var byEnv)
        && byEnv.TryGetValue(env, out var byKey)
        && byKey.TryGetValue(key, out var fixtureValue))
    {
        return fixtureValue;
    }

    var fromEnv = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(fromEnv))
    {
        return fromEnv;
    }

    unresolved.Add($"{component}/{env}:{key}");
    return match.Value;
});

if (unresolved.Count > 0)
{
    Console.Error.WriteLine($"wire: {unresolved.Count} token(s) did not resolve for environment '{environment}':");
    foreach (var token in unresolved.Distinct())
    {
        Console.Error.WriteLine($"  @@wiring({token})@@");
    }
    Console.Error.WriteLine("wire: refusing to write a config with unresolved wiring.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, rendered);
Console.WriteLine($"wire: rendered {templatePath} → {outputPath}");
return 0;
