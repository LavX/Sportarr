import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import DisclosureSection from '../DisclosureSection';

describe('DisclosureSection', () => {
  it('keeps advanced controls closed until the user asks for them', async () => {
    const user = userEvent.setup();
    render(
      <DisclosureSection title="Guide sources" description="Manual XMLTV controls">
        <button>Refresh guide</button>
      </DisclosureSection>,
    );

    expect(screen.getByRole('button', { name: 'Refresh guide' })).not.toBeVisible();

    await user.click(screen.getByText('Guide sources'));

    expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeVisible();
  });
});
