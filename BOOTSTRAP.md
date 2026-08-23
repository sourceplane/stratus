# Bootstrap

The operator checklist — the things no hook and no token can perform for you.

Everything else is automated: `orun new` instantiates and rebrands the repo,
and the umbrella (`flows/phases/00-all/workflow.yaml`) takes it from an empty
product to a live, documented fleet.

## 1. An Azure subscription, connected to the workspace

Console → Integrations → **Azure**. Two postures, and the second needs nothing
from the platform:

- **OAuth admin consent** — available when the environment has a registered
  multi-tenant Entra application.
- **Service-principal paste** — always available. Run this against the
  subscription you want to deploy into and paste the JSON output verbatim:

  ```bash
  az ad sp create-for-rbac --name orun \
    --role Contributor \
    --scopes /subscriptions/<subscription-id> \
    --sdk-auth
  ```

  The platform envelopes it once and never reads it back; every issuance from
  it is narrowed to a template, TTL-bounded and ledgered.

`Contributor` on the subscription is the honest floor — the bootstrap creates a
resource group, managed identities and federated credentials, and role
assignments require it. Anything narrower fails at the first `terraform apply`.

## 2. Region capacity

Capacity is regional and subscriptions differ. If `foundation` fails with
`SkuNotAvailable` or `QuotaExceeded`, the fix is **a different region or a quota
increase — never a smaller SKU**. Event Hubs and Service Bus are Standard for
correctness: Basic has no Kafka endpoint and only the `$Default` consumer
group, so a fleet built on it deploys cleanly and cannot consume anything.

## 3. The workspace binding

`intent.yaml` ships with no `execution.state` block, so state is local and CI
passes no `--remote-state`. Bind the repo to an Orun Cloud workspace and the
block and the flag land together.

## 4. What the bootstrap writes back

The `wiring` step commits five identifiers into `intent.yaml` — registry name,
resource group, client id, tenant id, subscription id.

**None of them is a secret.** After `foundation` has applied, the deploy lane
authenticates by workload identity federation: GitHub mints a token per run and
Azure trusts this repository by name. There is nothing to rotate, and if you
find yourself wanting to store an Azure secret, something has gone wrong.

## 5. Names are derived, and globally unique

The registry, Key Vault, Postgres server and both messaging namespaces take a
suffix derived from the subscription id and the name prefix — deterministic, so
a destroyed environment rebuilds under the same names rather than orphaning
them. A collision therefore means another subscription already built this
product slug. Report it rather than appending digits.

## 6. Cost

Stage is deliberately small: Postgres Flexible Server `B_Standard_B1ms`, Basic
Redis, one Event Hubs throughput unit, Container Apps on consumption so the
services scale to zero between deploys. `data-plane` exposes `postgresSku` and
`postgresStorageMb` as parameters — prod moves to General Purpose by changing
them, not by editing terraform.
