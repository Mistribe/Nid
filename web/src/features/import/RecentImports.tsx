import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatInstant } from '@/features/admin/format';
import { StepCard } from './StepCard';
import { ACTIVE_STATES, effectiveState, useStateLabel } from './states';
import '@/locales/import';

/** The person's recent sessions (import-format.md §11.1, `GET /imports`); an active one can be resumed. */
export function RecentImports() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const label = useStateLabel();
  const list = $api.useQuery('get', '/api/v1/platform/imports');

  if (list.error) return <ProblemMessage problem={present(toProblem(list.error))} />;
  if (!list.data) {
    return (
      <div aria-busy="true" className="h-24 shimmer rounded-md bg-muted">
        <span className="sr-only">{t('import.loading')}</span>
      </div>
    );
  }
  if (list.data.length === 0) return null; // nothing to resume: the file step below is the invitation

  return (
    <StepCard>
    <section aria-labelledby="import-recent-title" className="space-y-2">
      <h2 id="import-recent-title" className="text-lg font-semibold">{t('import.recent_title')}</h2>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('import.started')}</TableHead>
            <TableHead scope="col">{t('import.state_column')}</TableHead>
            <TableHead scope="col">{t('import.action')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {list.data.map((session) => {
            const state = effectiveState(session);
            return (
              <TableRow key={session.id}>
                <TableCell>{formatInstant(session.createdAt)}</TableCell>
                <TableCell>{label(state)}</TableCell>
                <TableCell>
                  <Link to="/import/$id" params={{ id: session.id }} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
                    {ACTIVE_STATES.has(state) ? t('import.resume') : t('import.view')}
                  </Link>
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </section>
    </StepCard>
  );
}
