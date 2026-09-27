interface ChannelAttentionState {
  isEnabled: boolean;
  status: string;
  isSportsChannel: boolean;
  tvgId?: string | null;
  mappedLeagueIds?: number[] | null;
}

export { getVisibleSelectedIds } from './selectionPresentation';

export function getChannelAttentionReason(channel: ChannelAttentionState): string | null {
  if (!channel.isEnabled) return 'Disabled';
  if (channel.status === 'Offline' || channel.status === 'Error') return channel.status;
  if (!channel.isSportsChannel) return null;

  const needsGuide = !channel.tvgId?.trim();
  const needsLeague = !channel.mappedLeagueIds?.length;

  if (needsGuide && needsLeague) return 'Needs guide and league mapping';
  if (needsGuide) return 'Needs guide mapping';
  if (needsLeague) return 'Needs league mapping';
  return null;
}
