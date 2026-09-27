import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import IptvSettingsNav from '../IptvSettingsNav';

describe('IptvSettingsNav', () => {
  it('keeps provider, recording, and advanced options in one area', () => {
    render(
      <MemoryRouter initialEntries={['/iptv/settings/recording']}>
        <IptvSettingsNav />
      </MemoryRouter>,
    );

    expect(screen.getByRole('navigation', { name: 'IPTV options' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Providers' })).toHaveAttribute(
      'href',
      '/iptv/settings/providers',
    );
    expect(screen.getByRole('link', { name: 'Recording' })).toHaveAttribute(
      'href',
      '/iptv/settings/recording',
    );
    expect(screen.getByRole('link', { name: 'Advanced' })).toHaveAttribute(
      'href',
      '/iptv/settings/advanced',
    );
    expect(screen.getByRole('link', { name: 'Recording' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });
});
