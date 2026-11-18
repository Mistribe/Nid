import { screen, within } from '@testing-library/react';
import type { UserEvent } from '@testing-library/user-event';

/** Opens the select or combobox labelled `label` and clicks the option. */
export async function pick(user: UserEvent, label: string | RegExp, option: string | RegExp): Promise<void> {
  await user.click(screen.getByRole('combobox', { name: label }));
  const list = await screen.findByRole('listbox');
  await user.click(await within(list).findByRole('option', { name: option }));
}

/** Opens the year stepper's grid and clicks the year (its name starts with the year, then what the page has for it). */
export async function pickYear(user: UserEvent, year: string): Promise<void> {
  await user.click(screen.getByRole('button', { name: /changer d’année$/ }));
  await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: new RegExp(`^${year}`) }));
}
