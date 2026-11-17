import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { toast } from 'sonner';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { BoxList, copyText } from './BoxList';

const rows = [{ code: '1AJ', title: 'Salaires', owner: <span>Alice</span>, amount: '42000.00' }];
const stubClipboard = (writeText?: () => Promise<void>) => Object.defineProperty(navigator, 'clipboard', { value: writeText && { writeText }, configurable: true });

afterEach(() => vi.restoreAllMocks());

describe('BoxList', () => {
  it('shows the code, the label, the owner and the amount', async () => {
    const { container } = render(<BoxList label="2042" rows={rows} />);
    const terms = screen.getAllByRole('term');
    expect(terms[0]).toHaveTextContent('1AJ');
    expect(terms[1]).toHaveTextContent('Salaires');
    expect(container).toHaveTextContent('Alice');
    expect(container).toHaveTextContent('42 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('copies the server’s amount as typed on impots.gouv.fr', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    stubClipboard(writeText);
    const success = vi.spyOn(toast, 'success').mockReturnValue(1);
    render(<BoxList label="2042" rows={rows} />);
    await userEvent.click(screen.getByRole('button', { name: 'Copier la case 1AJ' }));
    expect(writeText).toHaveBeenCalledWith('42000');
    await waitFor(() => expect(success).toHaveBeenCalledWith('Case 1AJ copiée : 42000'));
    expect(copyText('1234.50')).toBe('1234,5');
    expect(copyText('-500.00')).toBe('-500');
  });

  it('names a 2044 line after its list', () => {
    render(<BoxList label="Studio" rows={[{ code: '211', title: 'Loyers', amount: '1.00' }]} />);
    expect(screen.getByRole('button', { name: 'Copier la ligne 211 (Studio)' })).toBeInTheDocument();
  });

  it.each([['no clipboard', undefined], ['a rejecting clipboard', () => Promise.reject(new Error('no'))]])('says so when the copy fails: %s', async (_n, writeText) => {
    stubClipboard(writeText);
    const error = vi.spyOn(toast, 'error').mockReturnValue(1);
    render(<BoxList label="2042" rows={rows} />);
    await userEvent.click(screen.getByRole('button', { name: 'Copier la case 1AJ' }));
    await waitFor(() => expect(error).toHaveBeenCalledWith('Copie impossible : recopiez le montant à la main.'));
  });

  it('says so when the clipboard throws at once', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => { throw new Error('denied'); } }, configurable: true });
    const error = vi.spyOn(toast, 'error').mockReturnValue(1);
    render(<BoxList label="2042" rows={rows} />);
    await userEvent.click(screen.getByRole('button', { name: 'Copier la case 1AJ' }));
    expect(error).toHaveBeenCalledWith('Copie impossible : recopiez le montant à la main.');
  });

  it('renders two rows with the same code', () => {
    render(<BoxList label="2042" rows={[...rows, ...rows]} />);
    expect(screen.getAllByRole('button', { name: 'Copier la case 1AJ' })).toHaveLength(2);
  });

  it('keeps the copy buttons off paper', () => {
    render(<BoxList label="2042" rows={rows} />);
    expect(screen.getByRole('button', { name: 'Copier la case 1AJ' })).toHaveClass('print:hidden');
  });
});
