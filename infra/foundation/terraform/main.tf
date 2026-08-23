terraform {
  required_version = ">= 1.9.0"

  # State lives on the Orun platform. The runner exports TF_HTTP_* per job
  # (address = …/state/tfstate/{component}/{env}, the run token as password),
  # so this block needs no arguments and no -backend-config, and there is no
  # storage account to provision before the first apply can run.
  #
  # Declaring it is NOT optional: without a backend block terraform keeps
  # state on local disk, and a job workspace is ephemeral. Every apply would
  # then start from empty state and try to create resources that already
  # exist — which fails on the globally-unique names, on the SECOND run, long
  # after the first one looked fine.
  backend "http" {}

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {
    key_vault {
      # A soft-deleted vault blocks re-creating one with the same name, and the
      # name is deterministic — so a destroyed environment could never be
      # rebuilt without this.
      purge_soft_delete_on_destroy = true
    }
  }
}

# ── Injected by the composition's terraform-env step ─────────────────────────
# Every component parameter arrives as TF_VAR_<name>, plus the environment and
# component orun resolved — so a root never hardcodes which environment it is.

variable "environment" {
  type        = string
  description = "Environment this root is applied for."
}

variable "component" {
  type        = string
  description = "Orun component name owning this root."
}

variable "namespace" {
  type        = string
  description = "Resource name namespace (the product/org slug)."
}

variable "namespacePrefix" {
  type        = string
  description = "Per-environment name prefix, e.g. stg-."
}

variable "owner" {
  type        = string
  description = "GitHub owner of the product repo — the federated trust subject."
}

variable "repo" {
  type        = string
  description = "GitHub repo name — the federated trust subject."
}

variable "azureSubscriptionId" {
  type        = string
  description = "Subscription the resources land in. An identifier, not a secret."
}

variable "location" {
  type        = string
  default     = "westeurope"
  description = "Azure region."
}

locals {
  prefix = "${var.namespacePrefix}${var.namespace}"

  # Deterministic uniqueness suffix for the globally-named resources (ACR, Key
  # Vault). DERIVED, not random: a random_string regenerates when state is
  # lost, which would orphan every globally-named resource and then fail to
  # recreate it under the taken name.
  suffix = substr(sha256("${var.azureSubscriptionId}-${local.prefix}"), 0, 6)

  # ACR and storage names reject hyphens and are lowercase-only.
  compact = lower(replace(local.prefix, "-", ""))

  tags = {
    managedBy   = "orun"
    component   = var.component
    environment = var.environment
    repo        = "${var.owner}/${var.repo}"
  }
}

resource "azurerm_resource_group" "main" {
  name     = "${local.prefix}-rg"
  location = var.location
  tags     = local.tags
}

# ── Observability ────────────────────────────────────────────────────────────
# One workspace per environment. The Container Apps environment binds to it
# (platform root) and App Insights is workspace-based, so traces, container
# logs and metrics land in ONE place and can be correlated by trace id.

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${local.prefix}-logs"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "main" {
  name                = "${local.prefix}-appi"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
  tags                = local.tags
}

# ── Registry ─────────────────────────────────────────────────────────────────
# Admin user stays OFF: the deploy identity pushes with its AcrPush role and
# the apps pull with AcrPull, so there is no registry password anywhere.

resource "azurerm_container_registry" "main" {
  name                = "${local.compact}acr${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Standard"
  admin_enabled       = false
  tags                = local.tags
}

# ── Identities ───────────────────────────────────────────────────────────────
# TWO identities, because they are trusted by different things and outlive each
# other differently:
#
#   deploy  — assumed by GitHub Actions through workload identity federation.
#             No secret exists for it; the runner's OIDC token is the proof.
#   runtime — assigned to the Container Apps. It is how a service reaches
#             Postgres, Event Hubs and Service Bus without a connection string.

resource "azurerm_user_assigned_identity" "deploy" {
  name                = "${local.prefix}-deploy"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "runtime" {
  name                = "${local.prefix}-runtime"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tags                = local.tags
}

# The federated credentials. THIS is what removes the last stored secret: after
# these exist, CI authenticates with a token GitHub mints for the run, and orun
# brokers nothing. One credential per subject — Entra matches the subject
# EXACTLY, so a branch and a pull request need separate entries.

resource "azurerm_federated_identity_credential" "main_branch" {
  name                = "gh-main"
  resource_group_name = azurerm_resource_group.main.name
  parent_id           = azurerm_user_assigned_identity.deploy.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:${var.owner}/${var.repo}:ref:refs/heads/main"
}

resource "azurerm_federated_identity_credential" "pull_request" {
  name                = "gh-pull-request"
  resource_group_name = azurerm_resource_group.main.name
  parent_id           = azurerm_user_assigned_identity.deploy.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  # PR lanes plan; they never apply. The plan still needs to READ Azure, which
  # is why the subject exists at all.
  subject = "repo:${var.owner}/${var.repo}:pull_request"
}

resource "azurerm_federated_identity_credential" "environment" {
  name                = "gh-env-${var.environment}"
  resource_group_name = azurerm_resource_group.main.name
  parent_id           = azurerm_user_assigned_identity.deploy.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = "https://token.actions.githubusercontent.com"
  subject             = "repo:${var.owner}/${var.repo}:environment:${var.environment}"
}

# ── Role assignments ─────────────────────────────────────────────────────────

resource "azurerm_role_assignment" "deploy_contributor" {
  scope                = azurerm_resource_group.main.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.deploy.principal_id
}

resource "azurerm_role_assignment" "deploy_acr_push" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.deploy.principal_id
}

resource "azurerm_role_assignment" "runtime_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.runtime.principal_id
}

# The deploy identity must be able to ASSIGN the runtime identity to a
# Container App revision. Without this the deploy lane can create the app and
# then fail to give it an identity, which surfaces as a permission error deep
# in a revision update rather than at plan time.
resource "azurerm_role_assignment" "deploy_managed_identity_operator" {
  scope                = azurerm_user_assigned_identity.runtime.id
  role_definition_name = "Managed Identity Operator"
  principal_id         = azurerm_user_assigned_identity.deploy.principal_id
}

# ── Key Vault ────────────────────────────────────────────────────────────────
# RBAC rather than access policies: role assignments are the same mechanism
# every other resource here uses, and access policies are the legacy model.

data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "main" {
  name                       = "${local.compact}kv${local.suffix}"
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  tags                       = local.tags
}

resource "azurerm_role_assignment" "runtime_kv_secrets" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.runtime.principal_id
}

# ── Outputs ──────────────────────────────────────────────────────────────────
# These names are the CONTRACT: the component's secretOutputs maps each one
# onto a published wiring key, which is what reaches a service's config through
# @@wiring(...)@@ without any resource id being committed.

output "resource_group" {
  value       = azurerm_resource_group.main.name
  description = "Resource group every other root and the deploy lane targets."
}

output "acr_name" {
  value       = azurerm_container_registry.main.name
  description = "Registry name for az acr build and the image reference."
}

output "log_analytics_workspace_id" {
  value       = azurerm_log_analytics_workspace.main.id
  description = "Workspace the Container Apps environment binds to."
}

output "managed_identity_client_id" {
  value       = azurerm_user_assigned_identity.runtime.client_id
  description = "Client id the apps authenticate to Postgres and messaging with."
}

output "deploy_identity_client_id" {
  value       = azurerm_user_assigned_identity.deploy.client_id
  description = "Client id CI federates to. An identifier, not a secret."
}

output "runtime_identity_id" {
  value       = azurerm_user_assigned_identity.runtime.id
  description = "Full resource id, for assigning the identity to a Container App."
}

output "application_insights_connection_string" {
  value       = azurerm_application_insights.main.connection_string
  description = "OTLP destination for the services' telemetry."
  sensitive   = true
}

output "key_vault_uri" {
  value       = azurerm_key_vault.main.vault_uri
  description = "Vault the runtime identity reads secrets from."
}
