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

# Injected by the composition's terraform-env step from the component and
# environment orun resolved, so a root never hardcodes which environment it is.
variable "environment" {
  type        = string
  description = "Environment this root is applied for."
}

variable "component" {
  type        = string
  description = "Orun component name owning this root."
}

# Resources land with the Azure Container Apps baseline. This root is valid and
# inert today: the small baseline deploys to Coolify, and its `verify` profile
# runs fmt only.
