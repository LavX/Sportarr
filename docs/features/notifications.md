# Notifications

Sportarr can notify you on grabs, imports, upgrades, health issues, DVR recordings, and completed EPG syncs. Configure providers under **Settings > Notifications**:

- **Discord**, **Telegram**, **Slack**, **Mattermost**, and **Pushover**
- **Notifiarr**, sent natively to notifiarr.com - see [Notifiarr](../integrations/notifiarr.md)
- **Gotify**, **Join**, **Pushbullet**, and **SimplePush**
- **Email (SMTP)**
- Generic **webhooks**, with optional username/password auth and custom headers
- **ntfy**, self-hosted or ntfy.sh
- **Apprise**, one endpoint covering 90+ services
- **Custom scripts** that run any executable on events, with event details passed as environment variables
- **Plex**, **Jellyfin**, **Emby**, and **Kodi** library-refresh connections - see [Plex](../integrations/plex.md), [Jellyfin](../integrations/jellyfin.md), [Emby](../integrations/emby.md), and [Kodi](../integrations/kodi.md)

## EPG sync completed

Webhook and Custom Script connections can enable **On EPG Sync Complete**. Sportarr sends one notification after an EPG source has downloaded, saved, and finished automatic channel mapping.

The webhook event type is `EpgSyncCompleted`. Its payload includes `epgSourceId`, `epgSourceName`, `channelCount`, `programCount`, `autoMappedChannelCount`, and `completedAt` in UTC.

Custom scripts receive the same values as `SPORTARR_EPGSOURCEID`, `SPORTARR_EPGSOURCENAME`, `SPORTARR_CHANNELCOUNT`, `SPORTARR_PROGRAMCOUNT`, `SPORTARR_AUTOMAPPEDCHANNELCOUNT`, and `SPORTARR_COMPLETEDAT`. `SPORTARR_EVENT_TYPE` is `OnEpgSyncCompleted`.
