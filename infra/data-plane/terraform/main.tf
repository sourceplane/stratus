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
  features {}
}

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
  description = "GitHub owner of the product repo."
}

variable "repo" {
  type        = string
  description = "GitHub repo name."
}

variable "azureSubscriptionId" {
  type        = string
  description = "Subscription the resources land in. An identifier, not a secret."
}

variable "postgresSku" {
  type        = string
  default     = "B_Standard_B1ms"
  description = "Flexible Server SKU. Burstable is the stage default; prod moves to General Purpose."
}

variable "postgresStorageMb" {
  type        = number
  default     = 32768
  description = "Flexible Server storage in MB (32 GiB minimum)."
}

locals {
  prefix = "${var.namespacePrefix}${var.namespace}"
  suffix = substr(sha256("${var.azureSubscriptionId}-${local.prefix}"), 0, 6)

  tags = {
    managedBy   = "orun"
    component   = var.component
    environment = var.environment
    repo        = "${var.owner}/${var.repo}"
  }
}

# The resource group comes from `foundation`. Read by NAME rather than through
# remote state: the name is deterministic from the same inputs, and a data
# source keeps these roots independently appliable — one root's state can be
# rebuilt without touching another's.
data "azurerm_resource_group" "main" {
  name = "${local.prefix}-rg"
}

# ── Postgres ─────────────────────────────────────────────────────────────────
# Entra authentication ONLY. password_auth_enabled = false is the load-bearing
# line: with it there is no administrator password to store, rotate or leak,
# and the services connect as their managed identity. It is also why no
# administrator_login/password is declared — the provider rejects them when
# password auth is off.

resource "azurerm_postgresql_flexible_server" "main" {
  name                = "${local.prefix}-pg-${local.suffix}"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = data.azurerm_resource_group.main.location
  version             = "16"
  sku_name            = var.postgresSku
  storage_mb          = var.postgresStorageMb
  zone                = "1"

  # Public networking with a firewall allowance for Azure services. Private
  # endpoints are the prod posture; they need a VNet and would make the stage
  # environment unreachable from a GitHub runner, which is where migrations run.
  public_network_access_enabled = true

  authentication {
    active_directory_auth_enabled = true
    password_auth_enabled         = false
  }

  tags = local.tags

  lifecycle {
    # Storage can only grow. Terraform would happily plan a shrink that the
    # API then refuses halfway through an apply.
    prevent_destroy = false
    ignore_changes  = [zone]
  }
}

# One database per bounded context would be the alternative; one database with
# a schema per context is what the baseline uses, because RLS keyed on tenant
# is the multi-tenancy boundary and three databases would triple the server
# cost without adding a security boundary.
resource "azurerm_postgresql_flexible_server_database" "main" {
  name      = "stratus"
  server_id = azurerm_postgresql_flexible_server.main.id
  collation = "en_US.utf8"
  charset   = "UTF8"

  lifecycle {
    prevent_destroy = false
  }
}

# Azure-internal callers (Container Apps, and the ACR build agents). The
# 0.0.0.0 sentinel is Azure's documented "allow Azure services", NOT the
# internet — the internet would be 0.0.0.0–255.255.255.255.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ── Redis ────────────────────────────────────────────────────────────────────
# Idempotency keys and rate-limit counters. Both are recomputable, so Basic is
# honest for stage: losing the cache costs a duplicate-suppression window, not
# data.

resource "azurerm_redis_cache" "main" {
  name                          = "${local.prefix}-redis-${local.suffix}"
  resource_group_name           = data.azurerm_resource_group.main.name
  location                      = data.azurerm_resource_group.main.location
  capacity                      = 0
  family                        = "C"
  sku_name                      = "Basic"
  non_ssl_port_enabled          = false
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true
  tags                          = local.tags
}

# ── Outputs ──────────────────────────────────────────────────────────────────

output "postgres_fqdn" {
  value       = azurerm_postgresql_flexible_server.main.fqdn
  description = "Host the services and the migration bundles connect to."
}

output "postgres_database" {
  value       = azurerm_postgresql_flexible_server_database.main.name
  description = "Database name."
}

output "redis_host" {
  value       = azurerm_redis_cache.main.hostname
  description = "Redis host for idempotency and rate-limit state."
}

output "redis_ssl_port" {
  value       = azurerm_redis_cache.main.ssl_port
  description = "TLS port; the non-TLS port is disabled."
}
