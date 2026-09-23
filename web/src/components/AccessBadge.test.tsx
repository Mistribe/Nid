import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { AccessBadge } from './AccessBadge';

describe('<AccessBadge>', () => {
  it.each([['private', 'Privé'], ['shared', 'Partagé'], ['dependent', 'Enfant']] as const)(
    'says %s in words, never by colour alone', async (access, label) => {
      const { container } = render(<AccessBadge access={access} />);
      expect(screen.getByText(label)).toBeInTheDocument();
      expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
      await expectNoAxeViolations(container);
    });
});
