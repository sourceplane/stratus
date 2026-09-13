#!/usr/bin/env bash
# The manifest must state what the flows DO (saas-bootstrap-console BC2/BC3).
#
# `blueprint.yaml` is the contract the platform's bootstrap door reads and the
# console renders screen for screen. A manifest that drifts from the flows is a
# console that lies — showing an operator secrets that will not be created, or a
# programme that will not be laid out, with nothing failing until a customer's
# build is already running.
#
# Every check compares the manifest to THE THING THAT DOES THE WORK, never to
# another copy of the same claim. `spec.source.tag` sat at `baseline-v1` against
# a live `baseline-v4` for three releases (and `baseline-v17` against
# `baseline-v27` in lumen — ten) because nothing compared it to anything that
# moved. A drift test between two files catches nothing.
#
# TWO SHAPES OF REPO, one test. A repo may carry more than one baseline:
# stratus serves both the `stratus` and `stratus-coolify` registry rows from one
# tree, with different briefs and umbrellas. So every `blueprint*.yaml` at the
# root is checked, and each is checked against ITS OWN declared umbrella rather
# than against a single assumed one.
#
# And a baseline need not have every mechanism. Stratus has no
# `flows/common/create-secrets.sh` and no `ensure-milestone` calls at all, so it
# declares no `secrets` and no `programme`. That is allowed — but only
# BOTH-OR-NEITHER, checked in both directions: if the mechanism exists the block
# must match it, and if the block exists the mechanism must be there to realize
# it. "Absent" is never permission to skip a check silently; it is a fact that
# must agree with the other side.
#
# bash + python3 + PyYAML + git. No network, no fakes, no credential.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"

echo "── blueprint manifests against the flows that realize them"
python3 - "$root" <<'PY'
import pathlib, re, sys

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required for the manifest contract test (pip install pyyaml)")

root = pathlib.Path(sys.argv[1])
problems = []
def bad(m): problems.append(m)

manifests = sorted(root.glob("blueprint*.yaml"))
if not manifests:
    sys.exit("no blueprint*.yaml at the repo root — this check has gone blind")

# The secrets mechanism is shared across a repo's baselines when it exists.
secrets_script = root / "flows/common/create-secrets.sh"
script_text = secrets_script.read_text() if secrets_script.exists() else None
script_secrets = {
    m.group(1): (m.group(2), m.group(3))
    for m in re.finditer(r"^create\s+([A-Z][A-Z0-9_]*)\s+(\S+)\s+\S+\s+(\S+)\s*$", script_text or "", re.M)
}
if script_text is not None and not script_secrets:
    bad("create-secrets.sh exists but no `create <KEY> <provider> <conn> <template>` calls "
        "were found — the script's shape changed and this check has gone blind")

checked = []
for path in manifests:
    name = path.name
    spec = (yaml.safe_load(path.read_text()) or {}).get("spec") or {}
    def B(m): bad(f"{name}: {m}")

    # ── the field that cannot be true ─────────────────────────────────────
    if "tag" in (spec.get("source") or {}):
        B("spec.source.tag is back. The registry pins the tag; this file is fetched at it.")

    # ── blocks every baseline must carry ──────────────────────────────────
    for key in ("source", "overview", "requires", "inputs", "bootstrap"):
        if key not in spec:
            B(f"spec.{key} is missing — the console renders it")

    # ── inputs: askedBy is the console/agent division of labour ───────────
    for i, field in enumerate(spec.get("inputs") or []):
        key = field.get("key", f"#{i}")
        asked = field.get("askedBy")
        if asked not in ("console", "agent"):
            B(f"inputs[{key}].askedBy is {asked!r} — must be 'console' or 'agent'")
        if asked == "console" and not field.get("pattern"):
            B(f"inputs[{key}] is asked by the console but declares no pattern")

    # ── secrets: BOTH or NEITHER, checked both ways ───────────────────────
    declared = {s.get("key"): (s.get("provider"), s.get("template")) for s in (spec.get("secrets") or [])}
    if script_text is None:
        if declared:
            B(f"declares {len(declared)} secret(s) but the repo has no "
              f"flows/common/create-secrets.sh to create them")
    else:
        if "secrets" not in spec:
            B("create-secrets.sh exists and creates keys, but the manifest declares no secrets")
        for k in sorted(set(script_secrets) - set(declared)):
            B(f"create-secrets.sh creates {k} and the manifest does not declare it")
        for k in sorted(set(declared) - set(script_secrets)):
            B(f"declares secret {k} and create-secrets.sh never creates it")
        for k in sorted(set(script_secrets) & set(declared)):
            if script_secrets[k] != declared[k]:
                B(f"{k}: manifest says provider/template {declared[k]}, the script uses {script_secrets[k]}")

    # ── the umbrella THIS manifest declares ───────────────────────────────
    boot = spec.get("bootstrap") or {}
    for field in ("umbrella", "agentBrief"):
        rel = boot.get(field)
        if not rel:
            B(f"bootstrap.{field} is missing")
        elif not (root / rel).exists():
            B(f"bootstrap.{field} names {rel}, which does not exist")

    umb_rel = boot.get("umbrella")
    umb = (root / umb_rel).read_text() if umb_rel and (root / umb_rel).exists() else ""

    # ── programme: BOTH or NEITHER, against THIS manifest's umbrella ──────
    loop = re.search(r"for phase in ([0-9a-z\- ]+); do", umb)
    extra = re.findall(r'ensure-milestone "\$epic" ([0-9][0-9a-z\-]+)', umb)
    conditional = set(re.findall(r'!= "true" \]\s*\|\|.*?ensure-milestone "\$epic" ([0-9][0-9a-z\-]+)', umb))
    ensured = (loop.group(1).split() if loop else []) + extra

    prog = spec.get("programme")
    if not ensured:
        if prog:
            B(f"declares a programme but {umb_rel} ensures no milestones")
    else:
        if not prog:
            B(f"{umb_rel} ensures {len(ensured)} milestone(s) and the manifest declares no programme")
        else:
            ms = prog.get("milestones") or []
            if any(not isinstance(m, dict) or "name" not in m for m in ms):
                B("every programme.milestones entry must be a mapping with a `name` "
                  "(so a conditional one can carry its `when`)")
            else:
                names = [m["name"] for m in ms]
                dcond = {m["name"] for m in ms if m.get("when")}
                if names != ensured:
                    B(f"programme.milestones {names} != {umb_rel}'s {ensured}")
                if dcond != conditional:
                    B(f"conditional milestones disagree: manifest says {sorted(dcond) or '[]'}, "
                      f"the umbrella guards {sorted(conditional) or '[]'}")
            slug = re.search(r'epicslug:\s*\n\s*type: string\s*\n\s*default: "([^"]+)"', umb)
            if slug and prog.get("epicSlug") != slug.group(1):
                B(f"programme.epicSlug {prog.get('epicSlug')!r} != the umbrella's default {slug.group(1)!r}")

    # ── the brief and the umbrella must be a MATCHED PAIR ─────────────────
    # A repo with two baselines can point a manifest at the other one's
    # umbrella and nothing else would notice: both files exist, and if neither
    # lays out milestones the programme check passes vacuously. That is exactly
    # how the stratus-coolify REGISTRY row came to name the Azure brief. The
    # coupling that catches it is real and already in the tree: each brief
    # names the umbrella it tells the agent to run.
    brief_rel = boot.get("agentBrief")
    if umb_rel and brief_rel and (root / brief_rel).exists():
        brief = (root / brief_rel).read_text()
        umb_dir = str(pathlib.PurePosixPath(umb_rel).parent)
        named = set(re.findall(r"flows/phases/[0-9A-Za-z._\-]+", brief))
        if named and umb_dir not in named:
            B(f"bootstrap.agentBrief ({brief_rel}) tells the agent to run "
              f"{sorted(named)}, but bootstrap.umbrella is {umb_rel} — "
              f"the brief and the umbrella are not a matched pair")

    # ── verify is optional, but must not name an input that is not there ──
    input_keys = {f.get("key") for f in (spec.get("inputs") or [])} | {"env"}
    for url in ((spec.get("verify") or {}).get("urls") or []):
        for ph in re.findall(r"\{([a-zA-Z0-9_]+)\}", url):
            if ph not in input_keys:
                B(f"verify url interpolates {{{ph}}}, which is not an input")

    checked.append((name, len(declared), len((prog or {}).get("milestones") or [])))

# Two baselines in one repo must not claim the same brief or umbrella — a
# copy-paste that would silently give two registry rows one identity.
for field in ("umbrella", "agentBrief"):
    seen = {}
    for path in manifests:
        boot = ((yaml.safe_load(path.read_text()) or {}).get("spec") or {}).get("bootstrap") or {}
        v = boot.get(field)
        if v and v in seen:
            bad(f"{path.name} and {seen[v]} both declare bootstrap.{field} = {v}")
        elif v:
            seen[v] = path.name

if problems:
    print("FAIL: a manifest and the flows disagree:", file=sys.stderr)
    for p in problems:
        print(f"  - {p}", file=sys.stderr)
    sys.exit(1)

for name, ns, nm in checked:
    print(f"   {name}: {ns} secret(s), {nm} milestone(s) agree with the flows")
PY

echo "manifest.test.sh: ok"
