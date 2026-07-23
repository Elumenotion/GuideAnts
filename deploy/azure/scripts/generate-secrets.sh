#!/usr/bin/env bash
# Generate deployment secrets for GuideAnts Azure slim profile.
set -euo pipefail

KEY_VAULT_NAME=""
QUIET=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --key-vault-name) KEY_VAULT_NAME="$2"; shift 2 ;;
    --quiet) QUIET=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done

rand_token() {
  openssl rand -base64 48 | tr -d '/+=' | head -c 48
}

rand_base64_key() {
  openssl rand -base64 32
}

JWT_SIGNING_KEY="$(rand_token)"
SETTINGS_SECRETS_KEY="$(rand_base64_key)"
SCRIPT_AGENT_TOKEN="$(rand_token)"
SCRIPT_AGENT_ADMIN_TOKEN="$(rand_token)"
DOCUMENTSERVER_JWT_SECRET="$(rand_token)"

if [[ -n "$KEY_VAULT_NAME" ]]; then
  [[ "$QUIET" -eq 0 ]] && echo "[INFO] Writing secrets to Key Vault '$KEY_VAULT_NAME'..."
  az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "jwt-signing-key" --value "$JWT_SIGNING_KEY" --output none
  az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "settings-secrets-key-azure-deploy" --value "$SETTINGS_SECRETS_KEY" --output none
  az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "script-agent-token" --value "$SCRIPT_AGENT_TOKEN" --output none
  az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "script-agent-admin-token" --value "$SCRIPT_AGENT_ADMIN_TOKEN" --output none
  az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "documentserver-jwt-secret" --value "$DOCUMENTSERVER_JWT_SECRET" --output none
  [[ "$QUIET" -eq 0 ]] && echo "[SUCCESS] Secrets written to Key Vault."
fi

cat <<EOF
{"jwtSigningKey":"$JWT_SIGNING_KEY","settingsSecretsKey":"$SETTINGS_SECRETS_KEY","scriptAgentToken":"$SCRIPT_AGENT_TOKEN","scriptAgentAdminToken":"$SCRIPT_AGENT_ADMIN_TOKEN","documentServerJwtSecret":"$DOCUMENTSERVER_JWT_SECRET"}
EOF
