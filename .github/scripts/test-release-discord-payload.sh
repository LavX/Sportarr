#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

payload=$(DISCORD_UPDATES_ROLE_ID=1551809232091877466 \
  TITLE='New Release - v4.1.8' DESCRIPTION='Release notes' TIMESTAMP='2026-09-26T00:00:00.000Z' \
  bash .github/scripts/release-discord-payload.sh)
jq -e '.content == "<@&1551809232091877466>" and
       .allowed_mentions.parse == [] and
       .allowed_mentions.roles == ["1551809232091877466"] and
       .embeds[0].title == "New Release - v4.1.8"' <<< "$payload" >/dev/null

payload=$(DISCORD_UPDATES_ROLE_ID='' \
  TITLE='New Release - v4.1.8' DESCRIPTION='Release notes' TIMESTAMP='2026-09-26T00:00:00.000Z' \
  bash .github/scripts/release-discord-payload.sh)
jq -e 'has("content") | not' <<< "$payload" >/dev/null
jq -e '.allowed_mentions.parse == [] and .allowed_mentions.roles == []' <<< "$payload" >/dev/null

payload=$(DISCORD_UPDATES_ROLE_ID='bad@everyone' \
  TITLE='New Release - v4.1.8' DESCRIPTION='Release notes' TIMESTAMP='2026-09-26T00:00:00.000Z' \
  bash .github/scripts/release-discord-payload.sh)
jq -e 'has("content") | not' <<< "$payload" >/dev/null
jq -e '.allowed_mentions.parse == [] and .allowed_mentions.roles == []' <<< "$payload" >/dev/null

for workflow in dev-release.yml pr-release.yml republish-release.yml; do
  if grep -q 'DISCORD_UPDATES_ROLE_ID\|bash .github/scripts/release-discord-payload.sh' ".github/workflows/$workflow"; then
    exit 1
  fi
done

grep -q 'bash .github/scripts/test-release-discord-payload.sh' .github/workflows/dev-release.yml
