terraform {
  required_version = ">= 1.9.0"

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

locals {
  prefix = "${var.namespacePrefix}${var.namespace}"

  tags = {
    managedBy   = "orun"
    component   = var.component
    environment = var.environment
    repo        = "${var.owner}/${var.repo}"
  }
}

data "azurerm_resource_group" "main" {
  name = "${local.prefix}-rg"
}

data "azurerm_log_analytics_workspace" "main" {
  name                = "${local.prefix}-logs"
  resource_group_name = data.azurerm_resource_group.main.name
}

# ── The Container Apps environment ───────────────────────────────────────────
#
# The runtime every service revision lands in. It resolves service-to-service
# calls by internal DNS at RUNTIME, which is the reason this baseline has no
# equivalent of lumen's two-pass cycle-break: Cloudflare service bindings must
# resolve at DEPLOY time, so a dependency cycle there is a deploy-order
# problem. Here `http://identity` simply resolves inside the environment, and
# six mutually-referencing services deploy in any order.

resource "azurerm_container_app_environment" "main" {
  name                       = "${local.prefix}-cae"
  resource_group_name        = data.azurerm_resource_group.main.name
  location                   = data.azurerm_resource_group.main.location
  log_analytics_workspace_id = data.azurerm_log_analytics_workspace.main.id

  # Consumption-only: scale-to-zero between stage deploys is most of why this
  # environment is affordable to leave running.
  tags = local.tags
}

# Dapr components are declared HERE rather than per app: a component is
# environment-scoped, and declaring it beside one service would imply an
# ownership that does not exist.

output "container_apps_environment_id" {
  value       = azurerm_container_app_environment.main.id
  description = "Environment id every service revision deploys into."
}

output "container_apps_default_domain" {
  value       = azurerm_container_app_environment.main.default_domain
  description = "Suffix every service's ingress hostname is built from."
}

output "container_apps_static_ip" {
  value       = azurerm_container_app_environment.main.static_ip_address
  description = "Environment's static outbound IP — the address to allow-list."
}
