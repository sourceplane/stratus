# Stratus

**The .NET microservices baseline — multi-tenant SaaS on Azure Container Apps.**

A repo that knows how to become a product. Six ASP.NET Core services in clean
architecture behind a YARP gateway, EF Core per bounded context with migration
bundles, a transactional outbox over Event Hubs and Service Bus, terraform for
the whole Azure estate, and CI that converges the fleet on merge.

Sibling of [`lumen`](https://github.com/sourceplane/lumen) and
[`cirrus`](https://github.com/sourceplane/cirrus), which are TypeScript on
Cloudflare. This is the first baseline that is neither.

## Instantiate a product

```bash
orun new --blueprint repo-blueprint.yaml --out ../acme-cloud --run-hooks \
  --set repoName=acme-cloud --set productName="Acme Cloud" \
  --set pascalName=AcmeCloud --set productDomain=acme.dev
```

The `rebrand` hook rewrites the instance identity across the tree — 33
projects, their directories, their `.csproj` names and every namespace inside
them — and then **builds and tests the result**. A rename that produces a tree
which does not compile fails the hook rather than reaching whoever opens the
repo next.

Org-owned identity deliberately survives: the `sourceplane` GitHub org, the
`sourceplane.io` apiVersion, `ghcr.io/sourceplane` and the pinned
`stack-basalt` catalog reference belong to the platform. A fork that renamed
them would point its intent at a composition stack nobody publishes.

`BOOTSTRAP.md` carries the operator checklist no hook can perform.

## The shape

```
src/BuildingBlocks/     Result, Error, IClock; the Web base controller
src/Shared/             Contracts (the event/command vocabulary), Messaging
src/Services/           api-gateway · identity · tenancy · billing
                        notifier · projector
tests/                  per-service suites + the architecture suite
migrations/             one ef-migrate component per bounded context
infra/                  foundation · data-plane · messaging-plane · platform
tooling/                Wire (config rendering) · Rebrand (instantiation)
flows/                  the agent brief and the bootstrap umbrella
```

Per domain service, dependencies point inward only:

```
Domain → Application → Infrastructure ┐
                    → Web             ┴→ Host
```

Domain references nothing but the shared abstractions — no EF Core, no
ASP.NET — so an aggregate is testable with nothing but the language.
Application declares the ports and Infrastructure implements them. Web sees
Application only, so a controller physically cannot reach a `DbContext`. Host
is the composition root and the only project allowed to see both sides.

Twelve architecture tests enforce it, because layering is a claim until
something checks it.

## Two decisions worth knowing

**Messaging is Azure-native, and that is not a retreat from Kafka.** Event Hubs
speaks the **Kafka wire protocol** and Service Bus is **AMQP 1.0**, so moving
to Confluent Cloud, CloudAMQP or self-hosted brokers is a connection-string
change rather than a rewrite. Both namespaces are provisioned **Standard**, and
that is correctness rather than cost: the Kafka endpoint is a
Standard-and-above feature, and Basic permits only the `$Default` consumer
group, so the projector could not hold its own cursor.

**Nothing holds a long-lived cloud credential.** `foundation` creates managed
identities and the federated credentials that trust this repository; after it
has applied, CI authenticates with a token GitHub mints per run and the
services reach Postgres, Event Hubs and Service Bus as their managed identity.
Postgres has password authentication **disabled**. The three Azure values in
`intent.yaml` are identifiers, not secrets, and nothing here needs rotating.

## Development

```bash
dotnet build --configuration Release
dotnet test  --configuration Release
```

The whole fleet is one orun plan whose `dependsOn` edges encode the order.
Nothing is deployed by hand:

```bash
orun plan --trigger github-manual --output plan.json   # offline, no credentials
orun run  --plan plan.json
```

A pull request compiles every project, runs each service's own suite, rehearses
every migration against a throwaway Postgres and renders every config from a
committed fixture — **with no cloud credential at all**. Merging to `main`
applies terraform, deploys a revision per service and applies the migrations.
