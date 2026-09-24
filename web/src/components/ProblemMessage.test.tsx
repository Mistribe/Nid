import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { ProblemMessage } from './ProblemMessage';

describe('<ProblemMessage>', () => {
  it('announces the problem with its request id', async () => {
    const { container } = render(<ProblemMessage problem={{ kind: 'unexpected', requestId: '00-abc-01' }} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(screen.getByRole('alert')).toHaveTextContent('00-abc-01');
    await expectNoAxeViolations(container);
  });
});
