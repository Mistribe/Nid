import { fireEvent, render, screen } from '@testing-library/react';
import { wcagContrast } from 'culori';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { initials, monogramInk, NEUTRAL_COLOR, ProviderIcon } from './ProviderIcon';

const ICON = '/api/v1/subscriptions/providers/icons/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

// Every label colour of the pinned catalog (Mistribe/SubTracker-Data 882b195), and the neutral one.
const LABEL_COLORS = [
  '#4DB6AC', '#6400F6', '#64B5F6', '#7723AA', '#798600', '#7986CB', '#798900', '#81C784', '#9575CD', '#A1887F',
  '#A855B4', '#F06292', '#FFD54F', NEUTRAL_COLOR,
];

describe('<ProviderIcon>', () => {
  it("shows the icon from Nid's origin, as a decoration beside the name", async () => {
    const { container } = render(<p><ProviderIcon name="Netflix" iconUrl={ICON} color="#6400F6" /><span>Netflix</span></p>);

    const image = container.querySelector('img');
    expect(image).toHaveAttribute('src', ICON);
    expect(image).toHaveAttribute('alt', '');
    expect(screen.queryByTestId('provider-monogram')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a monogram on the colour of the first label when there is no icon', async () => {
    const { container } = render(<p><ProviderIcon name="Apple Music" iconUrl={null} color="#6400F6" /><span>Apple Music</span></p>);

    const monogram = screen.getByTestId('provider-monogram');
    expect(monogram).toHaveTextContent('AM');
    expect(monogram).toHaveAttribute('aria-hidden', 'true');
    expect(monogram).toHaveStyle({ backgroundColor: '#6400F6', color: '#FFFFFF' });
    expect(container.querySelector('img')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('falls back to the monogram when the image fails to load', () => {
    const { container } = render(<ProviderIcon name="Netflix" iconUrl={ICON} color="#F06292" />);

    fireEvent.error(container.querySelector('img')!);

    expect(screen.getByTestId('provider-monogram')).toHaveTextContent('N');
    expect(container.querySelector('img')).not.toBeInTheDocument();
  });

  it('uses the neutral colour when the label colour is missing or not #RRGGBB', () => {
    render(<><ProviderIcon name="Wow" color={undefined} /><ProviderIcon name="Old" color="#FFa855b4" /></>);

    for (const monogram of screen.getAllByTestId('provider-monogram'))
      expect(monogram).toHaveStyle({ backgroundColor: NEUTRAL_COLOR });
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

  // frontend/design-system.md §7.7: text reaches 4.5:1 on its background, measured like the theme pairs.
  it.each(LABEL_COLORS)('writes on %s with at least 4.5:1', (background) => {
    expect(wcagContrast(monogramInk(background), background)).toBeGreaterThanOrEqual(4.5);
  });
});
