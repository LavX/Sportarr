import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import TvGuideMobileControls from '../TvGuideMobileControls';

describe('TvGuideMobileControls', () => {
  it('groups guide actions and time navigation into a compact mobile toolbar', () => {
    const onSync = vi.fn();
    const onOptions = vi.fn();
    const onPrevious = vi.fn();
    const onNow = vi.fn();
    const onNext = vi.fn();
    const onFilters = vi.fn();

    render(
      <TvGuideMobileControls
        dateLabel="Tue, Sep 22 · 1:00 AM–1:00 PM"
        syncing={false}
        filtersOpen={false}
        onSync={onSync}
        onOptions={onOptions}
        onPrevious={onPrevious}
        onNow={onNow}
        onNext={onNext}
        onFilters={onFilters}
      />
    );

    expect(screen.getByRole('group', { name: 'Guide actions' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Guide time controls' })).toBeInTheDocument();
    expect(screen.getByText('Tue, Sep 22 · 1:00 AM–1:00 PM')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Sync EPG' }));
    fireEvent.click(screen.getByRole('button', { name: 'IPTV Options' }));
    fireEvent.click(screen.getByRole('button', { name: 'Previous 6 hours' }));
    fireEvent.click(screen.getByRole('button', { name: 'Now' }));
    fireEvent.click(screen.getByRole('button', { name: 'Next 6 hours' }));
    const filtersButton = screen.getByRole('button', { name: 'Filters' });
    expect(filtersButton).toHaveAttribute('aria-expanded', 'false');
    expect(filtersButton).toHaveAttribute('aria-controls', 'tv-guide-filters');
    fireEvent.click(filtersButton);

    expect(onSync).toHaveBeenCalledOnce();
    expect(onOptions).toHaveBeenCalledOnce();
    expect(onPrevious).toHaveBeenCalledOnce();
    expect(onNow).toHaveBeenCalledOnce();
    expect(onNext).toHaveBeenCalledOnce();
    expect(onFilters).toHaveBeenCalledOnce();
  });
});
