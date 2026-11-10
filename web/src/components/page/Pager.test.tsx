import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { pageOf } from '@/app/router';
import { Pager } from './Pager';

describe('Pager', () => {
  it('is hidden when everything fits on one page', () => {
    render(<Pager page={1} total={50} onPage={() => {}} />);
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  it('says where it is and steps back', async () => {
    const onPage = vi.fn();
    render(<Pager page={2} total={101} onPage={onPage} />);
    expect(screen.getByRole('navigation', { name: 'Pages' })).toBeVisible();
    expect(screen.getByText('Page 2 sur 3')).toBeVisible();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Précédent' }));
    expect(onPage).toHaveBeenCalledWith(1);
  });

  it('disables « Suivant » on the last page', () => {
    render(<Pager page={3} total={101} onPage={() => {}} />);
    expect(screen.getByRole('button', { name: 'Suivant' })).toBeDisabled();
  });

  it('reads the page of the URL: a whole number from 2', () => {
    expect([pageOf({ page: 3 }), pageOf({ page: '4' }), pageOf({ page: 1 }), pageOf({ page: 'x' }), pageOf({ page: 2.5 }), pageOf({})])
      .toEqual([3, 4, undefined, undefined, undefined, undefined]);
  });
});
