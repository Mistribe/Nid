import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { ComboboxField } from './ComboboxField';
import { CurrencyField } from './CurrencyField';
import { DateField } from './DateField';
import { Field } from './Field';
import { Segmented } from './Segmented';
import { SelectField } from './SelectField';

const currencies = ['EUR', 'USD', 'GBP'].map((c) => ({ value: c, label: c }));

function DateHarness({ onChange, max }: { onChange: (v: string) => void; max?: string }) {
  const [v, setV] = useState('');
  return <Field label="Date">{(c) => <DateField {...c} max={max} value={v} onChange={(x) => { setV(x); onChange(x); }} />}</Field>;
}

describe('form controls', () => {
  beforeAll(() => Promise.all([import('./DatePanel'), import('./ComboboxList')]));
  const tz = process.env.TZ;
  afterEach(() => { if (tz === undefined) delete process.env.TZ; else process.env.TZ = tz; });

  it('SelectField maps the empty option to an empty value', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    const { container } = render(
      <Field label="Bien">{(c) => (
        <SelectField {...c} value="p1" onValueChange={onValueChange}
          options={[{ value: '', label: 'Aucun' }, { value: 'p1', label: 'BJ' }]} />
      )}</Field>,
    );
    await pick(user, 'Bien', 'Aucun');
    expect(onValueChange).toHaveBeenCalledWith('');
    await expectNoAxeViolations(container);
  });

  // In a form Radix puts a hidden <select> after the trigger: `space-y-*` (margin under every child but the last) gave the
  // trigger 4 px more under it than any other field.
  it('Field spaces its parts from the top, so a SelectField in a form gets no phantom margin under its trigger', () => {
    render(<form><Field label="Type">{(c) => <SelectField {...c} value="a" onValueChange={() => {}} options={[{ value: 'a', label: 'A' }]} />}</Field></form>);
    const trigger = screen.getByRole('combobox', { name: 'Type' });
    const root = trigger.parentElement!;
    expect(trigger.nextElementSibling?.tagName, 'Radix’s hidden select').toBe('SELECT');
    expect(root.className).not.toMatch(/space-y-/);
    expect(root).toHaveClass('[&>*+*]:mt-1');
    expect(trigger.className).not.toMatch(/(^|\s)(m|my|mb)-/);
  });

  it('ComboboxField filters by typed text', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    const { container } = render(
      <Field label="Devise">{(c) => (
        <ComboboxField {...c} value="" onValueChange={onValueChange} options={currencies}
          searchPlaceholder="Rechercher" empty="Aucun résultat" />
      )}</Field>,
    );
    await user.click(screen.getByRole('combobox', { name: 'Devise' }));
    await user.type(await screen.findByRole('combobox', { name: 'Rechercher' }), 'us');
    expect(screen.queryByRole('option', { name: 'EUR' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('option', { name: 'USD' }));
    expect(onValueChange).toHaveBeenCalledWith('USD');
    await expectNoAxeViolations(container);
  });

  it('ComboboxField in server mode shows the options as given and reports the typed text', async () => {
    const user = userEvent.setup();
    const onSearch = vi.fn();
    render(<ComboboxField aria-label="Ajouter" value="" onValueChange={() => {}} options={currencies} onSearch={onSearch}
      more="Précisez" searchPlaceholder="Rechercher" empty="Aucun résultat" />);
    await user.click(screen.getByRole('combobox', { name: 'Ajouter' }));
    await user.type(await screen.findByRole('combobox', { name: 'Rechercher' }), 'us');
    expect(onSearch).toHaveBeenLastCalledWith('us');
    expect(screen.getByRole('option', { name: 'EUR' })).toBeInTheDocument(); // not filtered here
    expect(screen.getByText('Précisez')).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(onSearch).toHaveBeenLastCalledWith(''); // a closed list forgets its text
  });

  it('ComboboxField in server mode keeps two options with the same label apart and picks the second', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<ComboboxField aria-label="Ajouter" value="" onValueChange={onValueChange} onSearch={() => {}}
      options={[{ value: 'a', label: 'Taux' }, { value: 'b', label: 'Taux' }]} searchPlaceholder="Rechercher" empty="Aucun résultat" />);
    await user.click(screen.getByRole('combobox', { name: 'Ajouter' }));
    const options = await screen.findAllByRole('option', { name: 'Taux' });
    expect(options).toHaveLength(2);
    await user.click(options[1] as HTMLElement);
    expect(onValueChange).toHaveBeenCalledWith('b');
  });

  it('CurrencyField finds a currency by its code or its name, and emits the code', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<Field label="Devise">{(c) => <CurrencyField {...c} value="" onValueChange={onValueChange} />}</Field>);
    await user.click(screen.getByRole('combobox', { name: 'Devise' }));
    const search = await screen.findByRole('combobox', { name: 'Rechercher' });
    await user.type(search, 'eur');
    expect(screen.getByRole('option', { name: 'EUR — euro' })).toBeInTheDocument();
    await user.clear(search);
    await user.type(search, 'euro');
    expect(screen.getByRole('option', { name: 'EUR — euro' })).toBeInTheDocument();
    await user.clear(search);
    await user.type(search, 'USD');
    await user.click(screen.getByRole('option', { name: /^USD/ }));
    expect(onValueChange).toHaveBeenCalledWith('USD');
  });

  it.each(['Pacific/Honolulu', 'Pacific/Kiritimati'])('DateField keeps the picked day in %s', async (zone) => {
    process.env.TZ = zone;
    const user = userEvent.setup();
    const onChange = vi.fn();
    const { container } = render(<DateHarness onChange={onChange} />);
    await user.type(screen.getByRole('textbox', { name: 'Date' }), '18/10/2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
    await user.click(screen.getByRole('button', { name: /Choisir une date/ }));
    await user.click(await screen.findByRole('button', { name: /19/ }));
    expect(onChange).toHaveBeenLastCalledWith('2026-10-19');
    await expectNoAxeViolations(container);
  });

  it('DateField greys out the days after max in the calendar', async () => {
    const user = userEvent.setup();
    const { container } = render(<DateHarness onChange={vi.fn()} max="2026-10-18" />);
    await user.type(screen.getByRole('textbox', { name: 'Date' }), '17/10/2026');
    await user.click(screen.getByRole('button', { name: /Choisir une date/ }));
    expect(await screen.findByRole('button', { name: /18/ })).toBeEnabled();
    expect(screen.getByRole('button', { name: /19/ })).toBeDisabled();
    await expectNoAxeViolations(container);
  });

  it('DateField accepts jjmmaaaa typed without separators and shows it with slashes', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<DateHarness onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: 'Date' });
    await user.type(input, '01022025');
    expect(onChange).toHaveBeenLastCalledWith('2025-02-01');
    expect(input).toHaveValue('01/02/2025');
    expect(input).not.toHaveAttribute('inputmode');
  });

  it('DateField names its calendar button after its field', () => {
    render(<>
      <Field label="Début">{(c) => <DateField {...c} value="" onChange={vi.fn()} />}</Field>
      <Field label="Fin">{(c) => <DateField {...c} value="" onChange={vi.fn()} />}</Field>
    </>);
    expect(screen.getByRole('button', { name: 'Choisir une date : Début' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Choisir une date : Fin' })).toBeInTheDocument();
  });

  it('DateField reads a pasted date in any common shape', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<DateHarness onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: 'Date' });
    await user.click(input);
    await user.paste('18 octobre 2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
    expect(input).toHaveValue('18/10/2026');
  });

  it('DateField leaves an unreadable paste as typed text', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<DateHarness onChange={onChange} />);
    await user.click(screen.getByRole('textbox', { name: 'Date' }));
    await user.paste('demain');
    expect(onChange).toHaveBeenLastCalledWith('demain');
  });

  it('DateField passes unparsable text through', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const { container } = render(<DateHarness onChange={onChange} />);
    await user.type(screen.getByRole('textbox', { name: 'Date' }), '31/02/2026');
    expect(onChange).toHaveBeenLastCalledWith('31/02/2026');
    await expectNoAxeViolations(container);
  });

  it('Segmented is a radio group', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    const { container } = render(
      <Segmented aria-label="Visibilité" value="shared" onValueChange={onValueChange}
        options={[{ value: 'private', label: 'Privé' }, { value: 'shared', label: 'Partagé' }]} />,
    );
    expect(screen.getByRole('radio', { name: 'Partagé' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Privé' }));
    expect(onValueChange).toHaveBeenCalledWith('private');
    await expectNoAxeViolations(container);
  });

  it('the Segmented track is the fill colour, outlined only under Increase Contrast', () => {
    render(<Segmented aria-label="Visibilité" value="a" onValueChange={() => {}} options={[{ value: 'a', label: 'A' }]} />);
    const track = screen.getByRole('radiogroup');
    expect(track).toHaveClass('bg-fill', 'contrast-more:border-input', 'forced-colors:border-[CanvasText]');
    expect(track).not.toHaveClass('bg-muted');
  });
});
