import { screen, within } from '@testing-library/react';
import type { UserEvent } from '@testing-library/user-event';

/** Opens the select or combobox labelled `label` and clicks the option. */
export async function pick(user: UserEvent, label: string | RegExp, option: string | RegExp): Promise<void> {
  await user.click(screen.getByRole('combobox', { name: label }));
  const list = await screen.findByRole('listbox');
  await user.click(await within(list).findByRole('option', { name: option }));
}
