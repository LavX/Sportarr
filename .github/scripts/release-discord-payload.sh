#!/usr/bin/env bash
set -euo pipefail

role_id="${DISCORD_UPDATES_ROLE_ID:-}"
if [[ "$role_id" =~ ^[0-9]+$ ]] && [[ "$role_id" =~ [1-9] ]]; then
  jq -n \
    --arg title "$TITLE" \
    --arg desc "$DESCRIPTION" \
    --arg ts "$TIMESTAMP" \
    --arg role_id "$role_id" \
    '{username: "Sportarr", content: ("<@&" + $role_id + ">"),
      allowed_mentions: {parse: [], roles: [$role_id]},
      embeds: [{title: $title, description: $desc, color: 14427686, timestamp: $ts}]}'
else
  jq -n \
    --arg title "$TITLE" \
    --arg desc "$DESCRIPTION" \
    --arg ts "$TIMESTAMP" \
    '{username: "Sportarr", allowed_mentions: {parse: [], roles: []},
      embeds: [{title: $title, description: $desc, color: 14427686, timestamp: $ts}]}'
fi
