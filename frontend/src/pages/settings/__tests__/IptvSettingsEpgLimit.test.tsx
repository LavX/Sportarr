import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import { renderWithProviders, userEvent } from '../../../test/test-utils';
import apiClient from '../../../api/client';
import IptvSettings from '../IptvSettings';

vi.mock('../../../api/client');
vi.mock('../../../hooks/useUISettings', () => ({
  useUISettings: () => ({ timezone: 'UTC' }),
}));

describe('IPTV EPG download limit', () => {
  const openRefreshSettings = async (user: ReturnType<typeof userEvent.setup>) => {
    await user.click(screen.getByText('Refresh schedule and limits'));
  };

  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState({}, '', '/iptv/settings/advanced');
    vi.mocked(apiClient.get).mockImplementation(async (url) => {
      if (url === '/settings') {
        return { data: { epgMaxDownloadSizeMb: 300 } } as never;
      }

      return { data: [] } as never;
    });
  });

  it('keeps provider bulk selection hidden until Manage is opened', async () => {
    const user = userEvent.setup();
    window.history.replaceState({}, '', '/iptv/settings/providers');
    vi.mocked(apiClient.get).mockImplementation(async (url) => {
      if (url === '/settings') return { data: { epgMaxDownloadSizeMb: 300 } } as never;
      if (url === '/iptv/sources') {
        return {
          data: [{
            id: 7,
            name: 'Sports provider',
            type: 'M3U',
            url: 'https://provider.example/playlist.m3u',
            isActive: true,
            channelCount: 42,
          }],
        } as never;
      }
      return { data: [] } as never;
    });

    renderWithProviders(<IptvSettings />);

    await screen.findByText('Sports provider');
    expect(screen.queryByTitle('Select for bulk delete')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Manage providers' }));

    expect(screen.getByTitle('Select for bulk delete')).toBeVisible();
  });

  it('shows the saved EPG download limit', async () => {
    const user = userEvent.setup();
    renderWithProviders(<IptvSettings />);
    await openRefreshSettings(user);

    const input = await screen.findByLabelText('EPG download limit (MB)');

    expect(input).toHaveValue(300);
  });

  it('allows the old limit to be cleared before typing a new value', async () => {
    const user = userEvent.setup();
    renderWithProviders(<IptvSettings />);
    await openRefreshSettings(user);
    const input = await screen.findByLabelText('EPG download limit (MB)');

    await user.clear(input);

    expect(input).toHaveValue(null);
  });

  it('saves a whole number when a decimal is entered', async () => {
    const user = userEvent.setup();
    renderWithProviders(<IptvSettings />);
    await openRefreshSettings(user);
    const input = await screen.findByLabelText('EPG download limit (MB)');

    await user.clear(input);
    await user.type(input, '300.5');
    await user.click(screen.getByRole('button', { name: 'Save Settings' }));

    await waitFor(() => {
      expect(apiClient.put).toHaveBeenCalledWith('/settings', expect.objectContaining({
        epgMaxDownloadSizeMb: 300,
      }));
    });
  });

  it('blocks edits and saves until the existing settings load', async () => {
    const user = userEvent.setup();
    let resolveSettings!: (value: { data: { epgMaxDownloadSizeMb: number } }) => void;
    const settingsResponse = new Promise<{ data: { epgMaxDownloadSizeMb: number } }>((resolve) => {
      resolveSettings = resolve;
    });
    vi.mocked(apiClient.get).mockImplementation(async (url) => {
      if (url === '/settings') return settingsResponse as never;
      return { data: [] } as never;
    });
    renderWithProviders(<IptvSettings />);
    await openRefreshSettings(user);

    const input = screen.getByLabelText('EPG download limit (MB)');
    const saveButton = screen.getByRole('button', { name: 'Save Settings' });

    expect(input).toBeDisabled();
    expect(saveButton).toBeDisabled();

    await act(async () => {
      resolveSettings({ data: { epgMaxDownloadSizeMb: 300 } });
    });

    await waitFor(() => expect(input).toBeEnabled());
    expect(saveButton).toBeEnabled();
  });

  it('keeps saving disabled when the existing settings fail to load', async () => {
    const user = userEvent.setup();
    vi.mocked(apiClient.get).mockImplementation(async (url) => {
      if (url === '/settings') throw new Error('unavailable');
      return { data: [] } as never;
    });
    renderWithProviders(<IptvSettings />);
    await openRefreshSettings(user);

    expect(await screen.findByText('Unable to load these settings. Reload the page to try again.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save Settings' })).toBeDisabled();
  });
});
