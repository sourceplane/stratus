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

data "azurerm_resource_group" "main" {
  name = "${local.prefix}-rg"
}

# ── Event Hubs: the durable event backbone ───────────────────────────────────
#
# STANDARD, and the tier is NOT a cost preference — it is a correctness
# requirement. The Kafka wire-protocol endpoint that lets Confluent.Kafka
# clients talk to Event Hubs unchanged is a Standard-and-above feature; on
# Basic the namespace exists, accepts AMQP, and refuses every Kafka
# connection. Basic also caps retention at 1 day and forbids extra consumer
# groups, so the projector could not have its own cursor.
#
# This is the escape hatch the design leans on: because the protocol is
# Kafka's, moving to Confluent Cloud or a self-hosted cluster is a
# connection-string change rather than a rewrite.

resource "azurerm_eventhub_namespace" "main" {
  name                = "${local.prefix}-evhns-${local.suffix}"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = data.azurerm_resource_group.main.location
  sku                 = "Standard"
  capacity            = 1

  # Kafka is enabled by default on Standard, but stating it makes the
  # dependency visible to anyone reading this file rather than discovering it
  # from a connection refusal.
  local_authentication_enabled = false

  tags = local.tags
}

resource "azurerm_eventhub" "integration_events" {
  name              = "integration-events"
  namespace_id      = azurerm_eventhub_namespace.main.id
  partition_count   = 4
  message_retention = 7
}

# The projector reads with its own cursor, so a replay of the read models never
# disturbs any other consumer. This is the reason Basic is unusable: it permits
# only $Default.
resource "azurerm_eventhub_consumer_group" "projector" {
  name                = "projector"
  namespace_name      = azurerm_eventhub_namespace.main.name
  eventhub_name       = azurerm_eventhub.integration_events.name
  resource_group_name = data.azurerm_resource_group.main.name
}

# ── Service Bus: commands and work queues ────────────────────────────────────
#
# STANDARD for the same kind of reason: topics/subscriptions, sessions and
# scheduled delivery are Standard features, and Basic offers queues alone. The
# notifier's retry/backoff posture depends on scheduled delivery, and ordered
# per-tenant processing depends on sessions.

resource "azurerm_servicebus_namespace" "main" {
  name                          = "${local.prefix}-sbns-${local.suffix}"
  resource_group_name           = data.azurerm_resource_group.main.name
  location                      = data.azurerm_resource_group.main.location
  sku                           = "Standard"
  local_auth_enabled            = false
  public_network_access_enabled = true
  tags                          = local.tags
}

resource "azurerm_servicebus_queue" "notifications" {
  name         = "notifications"
  namespace_id = azurerm_servicebus_namespace.main.id

  # At-least-once with a real dead letter: the consumer is idempotent on
  # message id, so a redelivery is safe and a poison message stops after five
  # attempts instead of looping forever.
  max_delivery_count                      = 5
  dead_lettering_on_message_expiration    = true
  default_message_ttl                     = "P14D"
  lock_duration                           = "PT1M"
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
}

# ── Data-plane roles for the runtime identity ────────────────────────────────
# local_auth is OFF on both namespaces, so these assignments are the ONLY way
# in — there is no connection string with an embedded key to leak, which is the
# whole point.

data "azurerm_user_assigned_identity" "runtime" {
  name                = "${local.prefix}-runtime"
  resource_group_name = data.azurerm_resource_group.main.name
}

resource "azurerm_role_assignment" "runtime_eventhubs_receiver" {
  scope                = azurerm_eventhub_namespace.main.id
  role_definition_name = "Azure Event Hubs Data Receiver"
  principal_id         = data.azurerm_user_assigned_identity.runtime.principal_id
}

resource "azurerm_role_assignment" "runtime_eventhubs_sender" {
  scope                = azurerm_eventhub_namespace.main.id
  role_definition_name = "Azure Event Hubs Data Sender"
  principal_id         = data.azurerm_user_assigned_identity.runtime.principal_id
}

resource "azurerm_role_assignment" "runtime_servicebus" {
  scope                = azurerm_servicebus_namespace.main.id
  role_definition_name = "Azure Service Bus Data Owner"
  principal_id         = data.azurerm_user_assigned_identity.runtime.principal_id
}

# ── Outputs ──────────────────────────────────────────────────────────────────

output "eventhubs_namespace" {
  value       = azurerm_eventhub_namespace.main.name
  description = "Namespace host the Kafka endpoint is derived from."
}

output "servicebus_namespace" {
  value       = azurerm_servicebus_namespace.main.name
  description = "Namespace the AMQP client connects to."
}

output "eventhub_name" {
  value       = azurerm_eventhub.integration_events.name
  description = "The integration-events hub."
}

output "servicebus_queue" {
  value       = azurerm_servicebus_queue.notifications.name
  description = "The notifications command queue."
}
