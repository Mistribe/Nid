import { ErrorPage } from '@/components/ErrorPage';

/** Where an admin page sends a non-admin (`requireAdmin`), and any 403 (issue 362). */
export function ForbiddenPage() {
  return <ErrorPage problem={{ kind: 'forbidden' }} />;
}
