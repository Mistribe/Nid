import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { initials, ProviderIcon } from './ProviderIcon';

const ICON = '/api/v1/subscriptions/providers/icons/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

describe('<ProviderIcon>', () => {
  it("shows the icon from Nid's origin, as a decoration beside the name", async () => {
    const { container } = render(<p><ProviderIcon name="Netflix" iconUrl={ICON} color="#6400F6" /><span>Netflix</span></p>);

    const image = container.querySelector('img');
    expect(image).toHaveAttribute('src', ICON);
    expect(image).toHaveAttribute('alt', '');
    expect(screen.queryByTestId('provider-monogram')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a monogram beside the name when there is no icon', async () => {
    const { container } = render(<p><ProviderIcon name="Apple Music" iconUrl={null} color="#3679cd" /><span>Apple Music</span></p>);

    const monogram = screen.getByTestId('provider-monogram');
    expect(monogram).toHaveTextContent('AM');
    expect(monogram).toHaveAttribute('aria-hidden', 'true');
    expect(container.querySelector('img')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('a monogram is a tinted chip of the label’s slot, its initials in the text colour', () => {
    render(<ProviderIcon name="Apple Music" color="#3679cd" />);

    const monogram = screen.getByTestId('provider-monogram');
    expect(monogram.style.background).toContain('var(--chart-1)');
    expect(monogram.style.background).toContain('var(--card)');
    expect(monogram).toHaveClass('text-foreground');
  });

  it('falls back to the monogram when the image fails to load', () => {
    const { container } = render(<ProviderIcon name="Netflix" iconUrl={ICON} color="#F06292" />);

    fireEvent.error(container.querySelector('img')!);

    expect(screen.getByTestId('provider-monogram')).toHaveTextContent('N');
    expect(container.querySelector('img')).not.toBeInTheDocument();
  });

  it('a grey or missing colour gives a neutral chip', () => {
    render(<><ProviderIcon name="Wow" color={undefined} /><ProviderIcon name="Old" color="#FFa855b4" /><ProviderIcon name="Grey" color="#9E9E9E" /></>);

    for (const monogram of screen.getAllByTestId('provider-monogram')) {
      expect(monogram.style.background).toContain('var(--muted-foreground)');
      expect(monogram.style.background).not.toContain('--chart-');
    }
  });

  it.each([
    ['Netflix', 'N'],
    ['Apple Music', 'AM'],
    ['Red by SFR', 'RB'],
    ['  youtube   premium ', 'YP'],
    ['élan vital', 'ÉV'],
    ['Disney+', 'D'],
    ['', ''],
  ])('writes %j as %j', (name, expected) => {
    expect(initials(name)).toBe(expected);
  });
});
