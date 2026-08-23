using System.Text;
using System.Text.RegularExpressions;

namespace Rebrand;

/// <summary>
/// Applies the DEPLOY TARGET to a freshly instantiated tree.
///
/// ── Why this happens at instantiation rather than at run time ──
///
/// The obvious design is a profile: one repo, two lanes, the right one
/// selected per deployment. orun cannot express it — `profileRules[].when`
/// accepts `triggerRef` and nothing else (internal/model/intent.go), so
/// "same trigger, different profile depending on target" has no grammar. And
/// teaching orun about deploy targets would be the wrong fix: its scaffold
/// engine forbids ecosystem literals by architecture test, because a target is
/// a baseline's concept and not the tool's.
///
/// But the constraint turned out to point at the better answer anyway. A
/// product deploys to ONE place. A repo carrying both lanes runs both in CI —
/// twice the minutes, and one lane permanently unexercised against real
/// infrastructure, which is a green check that proves nothing. The choice is
/// known precisely when `orun new` runs, so it is made there, once, and the
/// instantiated repo has exactly one target with CI that tests it.
///
/// ── What "applying a target" actually is ──
///
/// Small, mechanical, and verifiable by `orun plan` afterwards:
///
///   coolify → delete the Azure terraform components, switch each service's
///             deploy profile to `deploy-coolify`, and set the messaging
///             provider to the self-hosted brokers.
///   azure   → leave the tree as authored. Stratus IS the Azure baseline;
///             this is a no-op that exists so the input is honest rather than
///             secretly one-way.
/// </summary>
public static class Target
{
    public const string Azure = "azure";
    public const string Coolify = "coolify";

    public static readonly string[] Supported = [Azure, Coolify];

    /// <summary>Azure-only infrastructure. Meaningless on a Coolify target,
    /// where Coolify itself provisions Postgres, Kafka and RabbitMQ as managed
    /// resources rather than terraform doing it.</summary>
    private static readonly string[] AzureInfraComponents =
        ["foundation", "data-plane", "messaging-plane", "platform"];

    public sealed record Result(int ComponentsSwitched, int DirectoriesRemoved, bool MessagingSwitched);

    public static Result Apply(string root, string target, bool dryRun, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.Equals(target, Azure, StringComparison.OrdinalIgnoreCase))
        {
            log.WriteLine("target: azure — the tree is already the Azure lane, nothing to change");
            return new Result(0, 0, false);
        }

        var switched = SwitchDeployProfiles(root, dryRun);
        var removed = RemoveAzureInfrastructure(root, dryRun);
        var messaging = SwitchMessagingProvider(root, dryRun);
        PruneIntent(root, dryRun);

        log.WriteLine(
            $"target: coolify — {(dryRun ? "would switch" : "switched")} {switched} service component(s) to "
            + $"deploy-coolify, {(dryRun ? "would remove" : "removed")} {removed} Azure infra component(s), "
            + $"messaging provider {(messaging ? "set to oss" : "unchanged")}");

        return new Result(switched, removed, messaging);
    }

    /// <summary>
    /// `profile: deploy` → `profile: deploy-coolify` in every service
    /// component's profileRules.
    ///
    /// Anchored to the profileRules line shape rather than a bare word: the
    /// string "deploy" appears in comments, in step ids and in `deployCommand`
    /// throughout these files, and a loose replace would corrupt all of them.
    /// </summary>
    private static int SwitchDeployProfiles(string root, bool dryRun)
    {
        var switched = 0;
        var servicesDir = Path.Combine(root, "src", "Services");
        if (!Directory.Exists(servicesDir)) return 0;

        foreach (var file in Directory.EnumerateFiles(servicesDir, "component.yaml", SearchOption.AllDirectories))
        {
            var original = File.ReadAllText(file);
            // `- profile: deploy` as its own list entry, end of line. Not
            // `deploy-coolify` (already switched) and not `deployCommand`.
            var updated = Regex.Replace(
                original,
                @"(?m)^(\s*-\s*profile:\s*)deploy(\s*)$",
                "$1deploy-coolify$2");

            // The comment ABOVE that line explains the Azure choice and ends
            // with "this baseline is the Azure one". Left alone it survives
            // into the Coolify tree as a confident statement of the opposite
            // of what the file now does — and a generated repo's comments are
            // read as fact, because nobody expects them to be stale. Rewritten
            // as a block so the replacement is one coherent paragraph rather
            // than four independently-patched lines.
            updated = Regex.Replace(
                updated,
                @"(?m)^(?<indent>\s*)# Azure Container Apps\. The catalog still carries deploy-coolify as a\r?\n"
                    + @"\s*# sibling profile of the same job template — switching target is a\r?\n"
                    + @"\s*# profile choice, not a fork — but this baseline is the Azure one, and\r?\n"
                    + @"\s*# its registry entry declares requiredIntegrations \[""azure""\]\.\r?\n",
                m =>
                {
                    var i = m.Groups["indent"].Value;
                    return $"{i}# Coolify. The catalog carries deploy and deploy-coolify as sibling\n"
                        + $"{i}# profiles of the SAME job template — switching target is a profile\n"
                        + $"{i}# choice, not a fork — and this tree was instantiated for Coolify,\n"
                        + $"{i}# whose registry entry declares requiredIntegrations [\"coolify\"].\n";
                });

            if (string.Equals(original, updated, StringComparison.Ordinal)) continue;

            switched++;
            if (!dryRun) File.WriteAllText(file, updated, new UTF8Encoding(false));
        }

        return switched;
    }

    /// <summary>
    /// Removes the terraform components. They are not merely unused on this
    /// target — they are unrunnable: every one needs an Azure subscription the
    /// product will never have, and leaving them in place means `orun plan`
    /// schedules four components that fail at their first credential read.
    /// </summary>
    private static int RemoveAzureInfrastructure(string root, bool dryRun)
    {
        var removed = 0;
        foreach (var name in AzureInfraComponents)
        {
            var dir = Path.Combine(root, "infra", name);
            if (!Directory.Exists(dir)) continue;

            removed++;
            if (!dryRun) Directory.Delete(dir, recursive: true);
        }

        // `infra/` itself goes only if nothing else is left in it, so a fork
        // that has added its own component there keeps it.
        var infra = Path.Combine(root, "infra");
        if (!dryRun
            && Directory.Exists(infra)
            && Directory.EnumerateFileSystemEntries(infra).FirstOrDefault() is null)
        {
            Directory.Delete(infra);
        }

        return removed;
    }

    /// <summary>
    /// Rewrites the config templates for the self-hosted brokers.
    ///
    /// This is more than setting `Messaging:Provider`, and the reason is worth
    /// stating: the Azure templates are built from `@@wiring(...)@@` tokens
    /// that resolve against the `data-plane` and `messaging-plane` components
    /// — the components this target has just DELETED. Left in place, the wiring
    /// step fails on a token whose component no longer exists, and the failure
    /// reads as a wiring bug rather than as "you chose a different target".
    ///
    /// So the tokens go, and the connection details arrive as environment
    /// variables instead. That is not a workaround, it is how Coolify supplies
    /// them: linking a Postgres or Kafka resource to an application injects
    /// `ConnectionStrings__Postgres`, `Messaging__KafkaBootstrapServers` and so
    /// on, which ASP.NET Core's environment provider already overlays on top of
    /// appsettings with no code involved.
    ///
    /// What stays in the file is what is NOT infrastructure: the queue name and
    /// the consumer group are the fleet's own vocabulary, identical on both
    /// targets, and belong in source rather than in a deployment's env.
    /// </summary>
    private static bool SwitchMessagingProvider(string root, bool dryRun)
    {
        var changed = false;
        var servicesDir = Path.Combine(root, "src", "Services");
        if (!Directory.Exists(servicesDir)) return false;

        foreach (var file in Directory.EnumerateFiles(
                     servicesDir, "appsettings.template.json", SearchOption.AllDirectories))
        {
            var original = File.ReadAllText(file);
            string updated;
            try
            {
                updated = RewriteConfig(original);
            }
            catch (System.Text.Json.JsonException)
            {
                // A template that is not valid JSON is not this tool's to fix,
                // and guessing at it with a regex is how a config silently
                // becomes wrong. Leave it and let the build say so.
                continue;
            }

            if (string.Equals(original, updated, StringComparison.Ordinal)) continue;

            changed = true;
            if (!dryRun) File.WriteAllText(file, updated, new UTF8Encoding(false));
        }

        // The component's `wiringComponents` names the same deleted components.
        foreach (var file in Directory.EnumerateFiles(servicesDir, "component.yaml", SearchOption.AllDirectories))
        {
            var original = File.ReadAllText(file);
            var updated = Regex.Replace(
                original,
                "(?m)^(\\s*wiringComponents:\\s*).*$",
                "$1\"\"");
            if (string.Equals(original, updated, StringComparison.Ordinal)) continue;

            changed = true;
            if (!dryRun) File.WriteAllText(file, updated, new UTF8Encoding(false));
        }

        return changed;
    }

    /// <summary>
    /// Takes the deleted components out of `intent.yaml`.
    ///
    /// Found by running it rather than by reading it: removing `infra/` left
    /// the intent still declaring it as a discovery root, and `orun new`
    /// failed its own validation gate with "failed to access discovery root
    /// infra". Deleting a component's files is only half of deleting the
    /// component.
    ///
    /// Line-based rather than a YAML round-trip, deliberately: this file is
    /// dense with comments that explain hard-won decisions — the `<no value>`
    /// trap, the secretEnv reasoning — and a serializer would silently discard
    /// every one of them.
    /// </summary>
    private static void PruneIntent(string root, bool dryRun)
    {
        var path = Path.Combine(root, "intent.yaml");
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path);
        var kept = new List<string>(lines.Length);
        var skippingBlock = false;
        var blockIndent = 0;

        foreach (var line in lines)
        {
            var indent = line.Length - line.TrimStart().Length;

            // Inside a block being dropped: keep skipping until the indent
            // returns to the block key's level or shallower.
            if (skippingBlock)
            {
                if (line.Trim().Length > 0 && indent <= blockIndent)
                {
                    skippingBlock = false;
                }
                else
                {
                    continue;
                }
            }

            var trimmed = line.Trim();

            // The discovery root whose directory no longer exists.
            if (trimmed is "- infra/" or "- infra") continue;

            // The composition binding for a type nothing declares any more.
            if (trimmed.StartsWith("terraform-azure:", StringComparison.Ordinal))
            {
                // A binding (`terraform-azure: stack-basalt`) is one line; a
                // parameterDefaults block is a key with children.
                if (trimmed == "terraform-azure:")
                {
                    skippingBlock = true;
                    blockIndent = indent;
                }
                continue;
            }

            kept.Add(line);
        }

        if (kept.Count == lines.Length) return;
        if (!dryRun) File.WriteAllLines(path, kept);
    }

    /// <summary>
    /// Drops every key whose value is a wiring token, sets the provider, and
    /// keeps the rest. Key-by-key rather than wholesale replacement so a
    /// service that has added its own settings does not lose them.
    /// </summary>
    internal static string RewriteConfig(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(
                   stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var section in document.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    section.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName(section.Name);
                writer.WriteStartObject();

                if (string.Equals(section.Name, "Messaging", StringComparison.Ordinal))
                {
                    writer.WriteString("Provider", "oss");
                }

                foreach (var setting in section.Value.EnumerateObject())
                {
                    // A wiring token names a component that no longer exists.
                    var value = setting.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? setting.Value.GetString()
                        : null;
                    if (value is not null && value.Contains("@@wiring(", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Azure-only names, meaningless once the provider is oss.
                    if (setting.Name is "EventHubsNamespace" or "ServiceBusNamespace" or "EventHubName")
                    {
                        continue;
                    }

                    if (string.Equals(setting.Name, "Provider", StringComparison.Ordinal))
                    {
                        continue; // already written above
                    }

                    setting.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
