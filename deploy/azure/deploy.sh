#!/usr/bin/env bash
# GuideAnts Azure Container Apps Deployment (azure-slim profile)
set -euo pipefail

ENVIRONMENT_NAME="dev"
LOCATION="East US 2"
APP_NAME_PREFIX="guideants"
GHCR_OWNER="elumenotion"
IMAGE_TAG="main"
CUSTOM_DOMAIN=""
SQL_ADMIN_PASSWORD=""
SUBSCRIPTION_ID=""
SKIP_MIGRATIONS=0
ONLY_INFRA=0
ONLY_APPS=0
SQL_AAD_ADMIN_OBJECT_ID=""

usage() {
  cat <<EOF
Usage: $0 [options]

Required:
  --sql-admin-password <password>   SQL admin password for migrations

Options:
  --environment-name <name>         Default: dev
  --location <region>               Default: East US 2
  --app-name-prefix <prefix>        Default: guideants
  --ghcr-owner <org>                Default: elumenotion
  --image-tag <tag>                 Default: main
  --custom-domain <domain>          Optional custom domain
  --subscription-id <id>            Azure subscription ID
  --skip-migrations                 Skip EF migrations
  --only-infra                      Deploy infrastructure only
  --only-apps                       Deploy container apps only (infra must exist)
  --sql-aad-admin-object-id <id>    Optional AAD SQL admin object ID
EOF
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --environment-name) ENVIRONMENT_NAME="$2"; shift 2 ;;
    --location) LOCATION="$2"; shift 2 ;;
    --app-name-prefix) APP_NAME_PREFIX="$2"; shift 2 ;;
    --ghcr-owner) GHCR_OWNER="$2"; shift 2 ;;
    --image-tag) IMAGE_TAG="$2"; shift 2 ;;
    --custom-domain) CUSTOM_DOMAIN="$2"; shift 2 ;;
    --sql-admin-password) SQL_ADMIN_PASSWORD="$2"; shift 2 ;;
    --subscription-id) SUBSCRIPTION_ID="$2"; shift 2 ;;
    --sql-aad-admin-object-id) SQL_AAD_ADMIN_OBJECT_ID="$2"; shift 2 ;;
    --skip-migrations) SKIP_MIGRATIONS=1; shift ;;
    --only-infra) ONLY_INFRA=1; shift ;;
    --only-apps) ONLY_APPS=1; shift ;;
    -h|--help) usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done

DEPLOY_ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$DEPLOY_ROOT/../.." && pwd)"
RESOURCE_GROUP="rg-${APP_NAME_PREFIX}-${ENVIRONMENT_NAME}"
SQL_DATABASE_NAME="guideants"

log() { echo "[INFO] $*"; }
ok() { echo "[SUCCESS] $*"; }
warn() { echo "[WARNING] $*" >&2; }
die() { echo "[ERROR] $*" >&2; exit 1; }

[[ -n "$SQL_ADMIN_PASSWORD" || "$ONLY_APPS" -eq 1 ]] || {
  KV_NAME="$(az keyvault list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || true)"
  if [[ -n "$KV_NAME" ]]; then
    SQL_ADMIN_PASSWORD="$(az keyvault secret show --vault-name "$KV_NAME" --name sql-admin-password --query value -o tsv 2>/dev/null || true)"
  fi
}
[[ -n "$SQL_ADMIN_PASSWORD" || "$ONLY_APPS" -eq 1 ]] || die "SqlAdminPassword is required for infrastructure deploy unless --only-apps is set (app-only updates skip SQL)."
command -v az >/dev/null || die "Azure CLI is not installed"
az account show >/dev/null 2>&1 || die "Not logged in to Azure. Run 'az login' first."

if [[ -n "$SUBSCRIPTION_ID" ]]; then
  az account set --subscription "$SUBSCRIPTION_ID"
fi

resolve_deployment_secrets() {
  if [[ "$ONLY_APPS" -eq 1 ]]; then
    log "Skipping bootstrap secret generation (--only-apps preserves existing Key Vault secrets)."
    return 0
  fi

  local kv_name
  kv_name="$(az keyvault list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || true)"
  if [[ -z "$kv_name" ]]; then
    log "Generating deployment secrets..."
    SECRETS_JSON="$("$DEPLOY_ROOT/scripts/generate-secrets.sh" --quiet)"
    JWT_SIGNING_KEY="$(echo "$SECRETS_JSON" | python -c "import json,sys; print(json.load(sys.stdin)['jwtSigningKey'])")"
    SETTINGS_SECRETS_KEY="$(echo "$SECRETS_JSON" | python -c "import json,sys; print(json.load(sys.stdin)['settingsSecretsKey'])")"
    SCRIPT_AGENT_TOKEN="$(echo "$SECRETS_JSON" | python -c "import json,sys; print(json.load(sys.stdin)['scriptAgentToken'])")"
    SCRIPT_AGENT_ADMIN_TOKEN="$(echo "$SECRETS_JSON" | python -c "import json,sys; print(json.load(sys.stdin)['scriptAgentAdminToken'])")"
    DOCUMENTSERVER_JWT_SECRET="$(echo "$SECRETS_JSON" | python -c "import json,sys; print(json.load(sys.stdin)['documentServerJwtSecret'])")"
    ok "Secrets generated"
    return 0
  fi

  log "Key Vault '$kv_name' exists; reusing stored bootstrap secrets..."
  JWT_SIGNING_KEY="$(az keyvault secret show --vault-name "$kv_name" --name jwt-signing-key --query value -o tsv)"
  SETTINGS_SECRETS_KEY="$(az keyvault secret show --vault-name "$kv_name" --name settings-secrets-key-azure-deploy --query value -o tsv)"
  SCRIPT_AGENT_TOKEN="$(az keyvault secret show --vault-name "$kv_name" --name script-agent-token --query value -o tsv)"
  SCRIPT_AGENT_ADMIN_TOKEN="$(az keyvault secret show --vault-name "$kv_name" --name script-agent-admin-token --query value -o tsv)"
  DOCUMENTSERVER_JWT_SECRET="$(az keyvault secret show --vault-name "$kv_name" --name documentserver-jwt-secret --query value -o tsv)"
  ok "Reusing existing Key Vault bootstrap secrets"
}

resolve_deployment_secrets

resolve_deployer_identity() {
  local signed_in_user_id
  signed_in_user_id="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
  if [[ -n "$signed_in_user_id" ]]; then
    DEPLOYER_OBJECT_ID="$signed_in_user_id"
    DEPLOYER_PRINCIPAL_TYPE="User"
    return 0
  fi

  local account_type account_name sp_id
  account_type="$(az account show --query user.type -o tsv)"
  account_name="$(az account show --query user.name -o tsv)"
  if [[ "$account_type" == "servicePrincipal" ]]; then
    sp_id="$(az ad sp show --id "$account_name" --query id -o tsv)"
    if [[ -n "$sp_id" ]]; then
      DEPLOYER_OBJECT_ID="$sp_id"
      DEPLOYER_PRINCIPAL_TYPE="ServicePrincipal"
      return 0
    fi
  fi

  die "Could not resolve deployer object ID. Sign in with 'az login' or 'az login --service-principal'."
}

if [[ "$ONLY_APPS" -eq 0 ]]; then
  resolve_deployer_identity
  log "Deploying infrastructure (Phase 1)..."
  az deployment sub create \
    --name "guideants-${ENVIRONMENT_NAME}-$(date +%Y%m%d-%H%M%S)" \
    --location "$LOCATION" \
    --template-file "$DEPLOY_ROOT/main.bicep" \
    --parameters \
      environmentName="$ENVIRONMENT_NAME" \
      location="$LOCATION" \
      resourceGroupName="$RESOURCE_GROUP" \
      appNamePrefix="$APP_NAME_PREFIX" \
      sqlDatabaseName="$SQL_DATABASE_NAME" \
      sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
      sqlAadAdminObjectId="$SQL_AAD_ADMIN_OBJECT_ID" \
      deployerObjectId="$DEPLOYER_OBJECT_ID" \
      jwtSigningKey="$JWT_SIGNING_KEY" \
      settingsSecretsKey="$SETTINGS_SECRETS_KEY" \
      scriptAgentToken="$SCRIPT_AGENT_TOKEN" \
      scriptAgentAdminToken="$SCRIPT_AGENT_ADMIN_TOKEN" \
      documentServerJwtSecret="$DOCUMENTSERVER_JWT_SECRET" \
    --output none
  ok "Infrastructure deployed"
fi

if [[ "$ONLY_INFRA" -eq 0 ]]; then
  log "Deploying container apps (Phase 2)..."
  az deployment group create \
    --name "guideants-apps-${ENVIRONMENT_NAME}-$(date +%Y%m%d-%H%M%S)" \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$DEPLOY_ROOT/apps.bicep" \
    --parameters \
      environmentName="$ENVIRONMENT_NAME" \
      location="$LOCATION" \
      appNamePrefix="$APP_NAME_PREFIX" \
      ghcrOwner="$GHCR_OWNER" \
      imageTag="$IMAGE_TAG" \
      customDomain="$CUSTOM_DOMAIN" \
      documentServerEnabled=true \
    --output none
  ok "Container apps deployed"

  SQL_SERVER_NAME="$(az sql server list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)"
  if [[ -n "$SQL_SERVER_NAME" && "$ONLY_APPS" -eq 0 ]]; then
    PUBLIC_IP="$(curl -fsS https://api.ipify.org)"
    EXISTING_RULE="$(az sql server firewall-rule list --resource-group "$RESOURCE_GROUP" --server "$SQL_SERVER_NAME" --query "[?startIpAddress=='$PUBLIC_IP'].name | [0]" -o tsv || true)"
    if [[ -z "$EXISTING_RULE" || "$EXISTING_RULE" == "null" ]]; then
      az sql server firewall-rule create --resource-group "$RESOURCE_GROUP" --server "$SQL_SERVER_NAME" --name allow-script-ip --start-ip-address "$PUBLIC_IP" --end-ip-address "$PUBLIC_IP" --output none
    fi

    if [[ "$SKIP_MIGRATIONS" -eq 0 ]]; then
      log "Applying EF migrations..."
      MIGRATION_CS="Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Initial Catalog=${SQL_DATABASE_NAME};User ID=sqladmin;Password=${SQL_ADMIN_PASSWORD};TrustServerCertificate=False;Encrypt=True;"
      if ! dotnet tool list --global | grep -q dotnet-ef; then
        dotnet tool install --global dotnet-ef
      fi
      pushd "$REPO_ROOT/src/server" >/dev/null
      dotnet ef database update \
        --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj \
        --startup-project GuideAntsApi/GuideAntsApi.csproj \
        --connection "$MIGRATION_CS"
      popd >/dev/null
      ok "Migrations applied"
    fi

    IDENTITY_NAME="id-${APP_NAME_PREFIX}-containers-${ENVIRONMENT_NAME}"
    CLIENT_ID="$(az identity show --resource-group "$RESOURCE_GROUP" --name "$IDENTITY_NAME" --query clientId -o tsv)"
    KV_NAME="$(az keyvault list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)"
    CONNECTION_STRING="Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Initial Catalog=${SQL_DATABASE_NAME};Authentication=Active Directory Managed Identity;User ID=${CLIENT_ID};TrustServerCertificate=False;Encrypt=True;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;"
    az keyvault secret set --vault-name "$KV_NAME" --name sql-connection-string --value "$CONNECTION_STRING" --output none \
      || die "Failed to update Key Vault secret 'sql-connection-string' on '$KV_NAME'."
    az containerapp update --name guideants-webapi-ui --resource-group "$RESOURCE_GROUP" --set-env-vars "DEPLOYMENT_TRIGGER=$(date +%s)" --output none \
      || die "Failed to force a new revision for guideants-webapi-ui."
    ok "Key Vault connection string updated and web API revision forced"
  elif [[ -n "$SQL_SERVER_NAME" && "$ONLY_APPS" -eq 1 ]]; then
    log "Skipping SQL setup, Key Vault connection string update, and web API revision bump (--only-apps)."
  fi

  pwsh -File "$DEPLOY_ROOT/scripts/upload-searxng-config.ps1" -ResourceGroupName "$RESOURCE_GROUP"

  FQDN="$(az containerapp show -n guideants-webapi-ui -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)"
  if [[ -n "$CUSTOM_DOMAIN" ]]; then URL="https://${CUSTOM_DOMAIN}"; else URL="https://${FQDN}"; fi
  echo ""
  echo "Application URL: $URL"
  echo "Next steps:"
  echo "  1. Register first user (becomes Admin)"
  echo "  2. Settings → Connections → configure cloud AI provider"
  echo "  3. Create project + chat smoke test"
fi

ok "Deployment completed"
