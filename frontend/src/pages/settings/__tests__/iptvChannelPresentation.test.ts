import { describe, expect, it } from 'vitest';
import { getChannelAttentionReason, getVisibleSelectedIds } from '../iptvChannelPresentation';

describe('getChannelAttentionReason', () => {
  it('flags channels that cannot be used', () => {
    expect(getChannelAttentionReason({
      isEnabled: false,
      status: 'Unknown',
      isSportsChannel: false,
      tvgId: null,
      mappedLeagueIds: [],
    })).toBe('Disabled');

    expect(getChannelAttentionReason({
      isEnabled: true,
      status: 'Offline',
      isSportsChannel: false,
      tvgId: 'espn.us',
      mappedLeagueIds: [1],
    })).toBe('Offline');
  });

  it('flags sports channels that automation could not map', () => {
    expect(getChannelAttentionReason({
      isEnabled: true,
      status: 'Online',
      isSportsChannel: true,
      tvgId: null,
      mappedLeagueIds: [],
    })).toBe('Needs guide and league mapping');

    expect(getChannelAttentionReason({
      isEnabled: true,
      status: 'Online',
      isSportsChannel: true,
      tvgId: 'espn.us',
      mappedLeagueIds: [],
    })).toBe('Needs league mapping');

    expect(getChannelAttentionReason({
      isEnabled: true,
      status: 'Online',
      isSportsChannel: true,
      tvgId: '   ',
      mappedLeagueIds: [1],
    })).toBe('Needs guide mapping');
  });

  it('leaves healthy mapped channels out of the attention queue', () => {
    expect(getChannelAttentionReason({
      isEnabled: true,
      status: 'Online',
      isSportsChannel: true,
      tvgId: 'espn.us',
      mappedLeagueIds: [1],
    })).toBeNull();
  });

  it('limits bulk work to selected rows that remain visible', () => {
    expect(getVisibleSelectedIds(new Set([1, 2, 3]), [{ id: 1 }, { id: 4 }])).toEqual([1]);
  });
});
