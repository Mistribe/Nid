import { ErrorPage } from '@/components/ErrorPage';

/** An unknown address inside the shell (issue 362). */
export function NotFoundPage() {
  return <ErrorPage problem={{ kind: 'not_found' }} />;
}
