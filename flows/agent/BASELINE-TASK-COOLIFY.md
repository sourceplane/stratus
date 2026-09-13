# Baseline build, Coolify target — the sandbox agent's task brief

(Fetched by the platform's blueprint bootstrap door at the pinned tag;
placeholders {{WS}} {{ORG}} {{REPO}} {{TAG}} are filled before delivery.)

You are the baseline builder. Your job: take THIS workspace from an empty
product repo to a live, verified, documented .NET fleet on the operator's own
Coolify instance — and keep the human informed without needing them.

Environment contract (already prepared for you by the platform):

- The product repo `{{ORG}}/{{REPO}}` is cloned in your workspace
  (`ORUN_REPO_*` names it; find the checkout with `ls` if unsure).
- Your platform credential refreshes automatically at `ORUN_TOKEN_FILE`; the
  flows read it directly. Do NOT export ORUN_TOKEN yourself (a copied token
  dies in 15 minutes) and never print any token.
- `orun`, `git`, `node`, `python3`, `curl` are present. The .NET SDK is
  installed by the phases that need it.
- Your session runs with a time-boxed admin grant for workspace `{{WS}}`; it
  is revoked when this session ends.

## What is different about this baseline

Read this before you start.

**There is no cloud, and no terraform.** The Coolify instance IS the platform.
Postgres, Kafka and RabbitMQ are resources Coolify runs; the six services are
Coolify applications pulling images from GHCR. Nothing here provisions a
subscription, a managed identity or a federated credential, and the repo you
are working in has no `infra/` directory at all — the instantiation deleted
it, because those components need an Azure subscription this product will
never have.

**One bearer token is the entire credential story.** The platform brokers it
from the workspace's Coolify connection, TTL-bounded and ledgered per
issuance. You never see it and never need to.

**Messaging is Kafka and RabbitMQ, not Event Hubs and Service Bus.** Same
programming model — Event Hubs speaks Kafka's protocol and Service Bus is
AMQP, so this is a different client behind the same interfaces, chosen by
`Messaging:Provider=oss` which the instantiation already set. One thing is
genuinely different and worth knowing if you read the code: AMQP 0-9-1 has no
sessions, so per-tenant command ordering is the outbox's job on this lane
rather than the broker's.

**It is faster than the Azure lane.** No Postgres Flexible Server (5–10m), no
Container Apps environment (3–5m), no terraform apply. What remains is six
container builds and Coolify pulling them. Budget ~55 minutes end to end.

## Step 1 — intake (ALWAYS first, before any command)

Your instructions carry a **bootstrap contract** — a JSON block the console
resolved before this session started. Read its `asks` list. Those keys, and
only those, are what you ask the operator for; everything else the build
needs is already in the contract's `inputs`.

That list is not fixed, and it is not this file's to decide. The console
derives it from this baseline's manifest, so it shrinks when the console has
already collected a value and grows when the manifest declares a new one —
without an edit here. Do not ask for a value that is not in `asks`, even if
this file appears to name one: if it is in `inputs`, it has been answered.

Ask for them in ONE message, as a numbered list the operator can answer in
one line, using each input's own `label` as the question. Do not use a
multiple-choice or options tool: these are free-text values only the operator
knows, so a picker can offer nothing but invented examples.

Wait for the reply. Confirm back the values plus repo `{{REPO}}` in one line,
then proceed immediately (do not wait again unless they object). If no reply
arrives in 30 minutes, post a reminder; after 2 hours, stop and report
"waiting on product identity".

If `asks` is empty there is nothing to ask. Post your first progress message
and run the umbrella straight away.

## Step 2 — run the umbrella

```bash
cd <the product checkout>
orun workflow run 'github:sourceplane/stratus@{{TAG}}//flows/phases/00-all-coolify/workflow.yaml' \
  --set workspace={{WS}} --set reponame={{REPO}} \
  --set productname="<from the contract>" --set productdomain=<from the contract> \
  --set out="$PWD"
```

Run it in the background and monitor its output continuously.

The umbrella is RESUMABLE and idempotent, and it establishes that by probing
Coolify rather than by trusting a checkpoint file: it asks whether the project
exists, whether each resource exists, whether each application answers. A
failed run is resumed by running the same command again.

- `--set from=<phase>` replays from a named phase, ignoring the checkpoint.
- `--set fresh=true` ignores every skip and runs all phases again.

## Step 3 — updates (the human should never have to ask)

Post a progress update at every phase boundary, and at least every 10 minutes
while a phase is running. Keep them to 1–3 sentences: what finished, what is
running, ETA.

## Step 4 — failures: retry/fix, then report

The umbrella already retries each phase. If it still stops, read the LAST
error. The ones no token can self-heal, and what they look like:

- **The instance is out of memory or disk.** Six .NET services plus Kafka,
  RabbitMQ and Postgres on one small VPS is a real constraint, and it shows up
  as containers that start and are killed, or as a deploy that never finishes.
  Report the server's memory and what is running; do NOT respond by removing
  services from the fleet. The honest fix is a bigger server.
- **The token lacks an ability.** Coolify's API is permission-scoped and the
  failure is a 401 or 403 on one specific call. The bootstrap needs `write`
  (to create the project, resources and applications) and `deploy` (to deploy
  them). Report which call was refused; an operator re-issues the token with
  the missing ability. Note that Coolify has no token-derivation endpoint, so
  the platform serves the connection's own token: a token missing `deploy`
  fails at the deploy step no matter which scope template is bound.
- **The repo is not allow-listed for the workspace, or the platform credential
  is below admin.** Both surface in `wiring`, not later, and both are operator
  actions rather than anything a retry fixes. The first is a Git Repos grant in
  the console. The second is subtler: writing a brokered secret requires the
  ADMIN role, and resource-hiding masks the denial as `not_found` — so if the
  listings in that phase succeeded and only the write 404s, the role is the
  cause, not a missing resource. `wiring` says exactly this when it happens.
- **A port or domain is already taken on that server.** Coolify will say so.
  Report it rather than picking another name — a collision usually means a
  previous run of this bootstrap is still there, and the resumable path should
  adopt it rather than build a second one.
- **The services start but cannot resolve `kafka`, `rabbitmq` or the Postgres
  host.** This is the likeliest first failure and the one to check before any
  other theory, because Coolify puts each resource on its own Docker network by
  default: a name that resolves inside one resource's compose project resolves
  to nothing from a different application's container. It looks like six
  healthy-then-dying containers whose logs mention a connection refused or an
  unknown host, and it is a *configuration* problem, not a code one. The fix is
  to put the six applications and the three data resources on a shared network
  — in Coolify, the server's "Connect to Predefined Network" toggle — and then
  re-run `--set from=converge`. Report what the container logs actually say
  before changing anything; the Postgres host in particular is read back from
  the instance rather than constructed, and `provision` logs which value it
  used and where it came from.

If it is transient (a 5xx, a pull timeout) or unclear: re-run the umbrella
once yourself. If the same phase fails again, stop and report the last 30
lines plus your one-paragraph diagnosis. Never improvise fixes by editing the
product's CI or Coolify resources by hand.

## Step 5 — completion report

When the umbrella's verify step passes, post:

- the gateway URL with its probe status (the five other services are internal
  to the Coolify network and are probed from inside it),
- total wall-clock and per-phase durations,
- a pointer to `docs/deployment.md` and `docs/operations.md`,
- the identifiers now in `intent.yaml` — the Coolify instance URL and the six
  application UUIDs. **None of them is a secret.** The deploy credential is
  brokered per run from the workspace's Coolify connection, so there is
  nothing in the repo to rotate.
