import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import { renderWithProviders, userEvent } from '../../test/test-utils';
import apiClient from '../../api/client';
import OnboardingWizard from '../OnboardingWizard';

vi.mock('../../api/client');
vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ login: vi.fn() }),
}));

async function openQualityStep(
  configured = false,
  preference: boolean | null = configured ? null : false,
  naming = false,
  settingsResponse?: Promise<{ data: { useRecommendedReleaseSettings: boolean | null } }>,
  currentNamingFormat = '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full} - {Sportarr Id}',
  namingResponses?: { settings?: Promise<unknown>; presets?: Promise<unknown> },
) {
  const user = userEvent.setup();
  vi.mocked(apiClient.get).mockImplementation(async (url) => {
    if (url === '/onboarding/status') return { data: { hasRootFolder: configured } } as never;
    if (url === '/trash/settings') return (settingsResponse ??
      Promise.resolve({ data: { useRecommendedReleaseSettings: preference } })) as never;
    if (url === '/qualityprofile') return { data: [
      { id: 1, name: 'WEB-1080p (Alternative)', isDefault: true },
      { id: 2, name: 'WEB-2160p (Alternative)', isDefault: false },
    ] } as never;
    if (url === '/settings') return (namingResponses?.settings ?? { data: {
      mediaManagementSettings: JSON.stringify({ standardFileFormat: currentNamingFormat, renameEpisodes: false }),
      securitySettings: '{}',
    } }) as never;
    if (url === '/trash/naming-presets?enableMultiPartEpisodes=true') return (namingResponses?.presets ?? { data: {
      file: naming ? {
        'plex-standard': { format: '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full} - {Sportarr Id}', description: 'Plex naming' },
        'full-details': { format: '{Series} - {Season}{Episode}{Part} - {Event Title} [{Quality Full}] {Sportarr Id}', description: 'Full details' },
      } : {},
    } }) as never;
    return { data: [] } as never;
  });
  renderWithProviders(<OnboardingWizard onClose={vi.fn()} onComplete={vi.fn()} />);
  await user.click(screen.getByRole('button', { name: 'Get started' }));
  for (let step = 0; step < 3; step += 1) {
    await user.click(screen.getByRole('button', { name: 'Skip Step' }));
  }
  expect(screen.getByRole('heading', { name: 'Choose your release setup' })).toBeVisible();
  return user;
}

describe('onboarding release preferences', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true } } as never);
  });

  it('keeps the seeded Standard setup when skipping on a new installation', async () => {
    const user = await openQualityStep();

    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
    expect(screen.getByRole('heading', { name: 'Connect your download client' })).toBeVisible();
  });

  it('applies recommendations only when the user chooses them', async () => {
    const user = await openQualityStep();

    await user.click(screen.getByRole('button', { name: /Recommended setup/i }));
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith(
      '/onboarding/release-preferences', { mode: 'recommended' }));
    expect(screen.getByRole('heading', { name: 'Connect your download client' })).toBeVisible();
  });

  it('does not alter release preferences when skipping a reopened guide', async () => {
    const user = await openQualityStep(true);

    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
  });

  it('does not reset an older install without a library folder', async () => {
    const user = await openQualityStep(false, null);

    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
  });

  it('does not reset Standard scores on an older install without a library folder', async () => {
    const user = await openQualityStep(false, false);

    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
  });

  it('does not reset Standard scores when an untouched step is saved', async () => {
    const user = await openQualityStep(false, false);

    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
  });

  it('keeps existing naming settings when the choice is untouched', async () => {
    const user = await openQualityStep(true, null, true);

    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    expect(apiClient.put).not.toHaveBeenCalledWith('/settings', expect.anything());
    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', expect.anything());
  });

  it('applies the recommended naming preset by default on a new installation', async () => {
    const user = await openQualityStep(false, false, true);

    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Apply this naming preset' })).toBeChecked());
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/settings', expect.anything()));
    const payload = vi.mocked(apiClient.put).mock.calls.find(([url]) => url === '/settings')?.[1] as {
      mediaManagementSettings: string;
    };
    const saved = JSON.parse(payload.mediaManagementSettings);
    expect(saved.standardFileFormat).toBe(
      '{Series} - {Season}{Episode}{Part} - {Event Title} [{Quality Full}] {Sportarr Id}');
    expect(saved.renameEpisodes).toBe(true);
  });

  it('waits for naming data before saving the default preset', async () => {
    let finishSettings!: (value: unknown) => void;
    let finishPresets!: (value: unknown) => void;
    const settings = new Promise<unknown>(resolve => { finishSettings = resolve; });
    const presets = new Promise<unknown>(resolve => { finishPresets = resolve; });
    const user = await openQualityStep(false, false, true, undefined, undefined,
      { settings, presets });
    const save = screen.getByRole('button', { name: 'Save & Next' });
    expect(save).toBeDisabled();

    await act(async () => finishSettings({ data: {
      mediaManagementSettings: JSON.stringify({ standardFileFormat: '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full} - {Sportarr Id}' }),
      securitySettings: '{}',
    } }));
    expect(save).toBeDisabled();

    await act(async () => finishPresets({ data: { file: {
      'plex-standard': { format: '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full} - {Sportarr Id}', description: 'Plex naming' },
      'full-details': { format: '{Series} - {Season}{Episode}{Part} - {Event Title} [{Quality Full}] {Sportarr Id}', description: 'Full details' },
    } } }));
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);
    await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/settings', expect.anything()));
  });

  it('keeps custom naming on a reopened guide unless the user opts in', async () => {
    const user = await openQualityStep(true, false, true, undefined, '{Event Title} - {Quality}');

    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Apply this naming preset' })).not.toBeChecked());
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    expect(apiClient.put).not.toHaveBeenCalledWith('/settings', expect.anything());
  });

  it('does not change naming when a new user skips despite the checked default', async () => {
    const user = await openQualityStep(false, false, true);

    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Apply this naming preset' })).toBeChecked());
    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.put).not.toHaveBeenCalledWith('/settings', expect.anything());
  });

  it('keeps a clicked choice when the settings request finishes late', async () => {
    let finishSettings!: (value: { data: { useRecommendedReleaseSettings: boolean | null } }) => void;
    const settingsResponse = new Promise<{ data: { useRecommendedReleaseSettings: boolean | null } }>(
      resolve => { finishSettings = resolve; });
    const user = await openQualityStep(true, null, false, settingsResponse);

    await user.click(screen.getByRole('button', { name: /Recommended setup/i }));
    await act(async () => finishSettings({ data: { useRecommendedReleaseSettings: false } }));
    expect(screen.getByRole('button', { name: /Recommended setup/i }))
      .toHaveAttribute('aria-pressed', 'true');
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith(
      '/onboarding/release-preferences', { mode: 'recommended' }));
  });

  it('does not show the old reset warning when Standard is selected', async () => {
    const user = await openQualityStep(true, null);
    const standard = screen.getByRole('button', { name: /Standard setup/i });
    expect(standard).toHaveAttribute('aria-pressed', 'false');

    await user.click(standard);

    expect(screen.queryByText(/resets any scores/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith(
      '/onboarding/release-preferences', { mode: 'standard' }));
  });

  it('explains which naming tokens help metadata matching', async () => {
    const user = await openQualityStep(false, false, true);

    await user.click(screen.getByRole('button', { name: /Standard setup/i }));

    expect(screen.queryByText(/resets any scores/i)).not.toBeInTheDocument();
    expect(screen.getByText(/season and episode numbers/i)).toBeVisible();
    expect(screen.getByText(/Sportarr Id/i)).toBeVisible();
  });

  it('does not undo an applied recommendation when revisiting and skipping', async () => {
    const user = await openQualityStep();
    await user.click(screen.getByRole('button', { name: /Recommended setup/i }));
    await user.click(screen.getByRole('button', { name: 'Save & Next' }));
    await user.click(screen.getByRole('button', { name: 'Back' }));

    await user.click(screen.getByRole('button', { name: 'Skip Step' }));

    expect(apiClient.post).toHaveBeenCalledWith(
      '/onboarding/release-preferences', { mode: 'recommended' });
    expect(apiClient.post).not.toHaveBeenCalledWith(
      '/onboarding/release-preferences', { mode: 'standard' });
  });
});
