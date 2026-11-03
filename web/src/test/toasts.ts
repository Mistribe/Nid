import { screen } from '@testing-library/react';

/** The live region of the shell's toasts: what an action announced (design-system.md §5). */
export const toasts = () => screen.getByRole('region', { name: /^Messages/ });
