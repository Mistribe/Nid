import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstant } from '@/features/admin/format';
import { ACTIVE_STATES, effectiveState, useStateLabel } from './states';

/** The person's recent sessions (import-format.md §11.1, `GET /imports`); an active one can be resumed. */
export function RecentImports() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const label = useStateLabel();
  const list = $api.useQuery('get', '/api/v1/platform/imports');

  if (list.error) return <ProblemMessage problem={present(toProblem(list.error))} />;
  if (!list.data) {
    return (
      <div aria-busy="true" className="h-24 animate-pulse rounded-md bg-muted">
        <span className="sr-only">{t('import.loading')}</span>
      </div>
    );
  }
  if (list.data.length === 0) return null; // nothing to resume: the file step below is the invitation

  return (
    <section aria-labelledby="import-recent-title" className="space-y-2">
      <h2 id="import-recent-title" className="text-lg font-semibold">{t('import.recent_title')}</h2>
      <table className="w-full text-left">
        <thead>
          <tr className="border-b">
            <th scope="col" className="py-2 pr-4">{t('import.started')}</th>
            <th scope="col" className="py-2 pr-4">{t('import.state_column')}</th>
            <th scope="col" className="py-2">{t('import.action')}</th>
          </tr>
        </thead>
        <tbody>
          {list.data.map((session) => {
            const state = effectiveState(session);
            return (
              <tr key={session.id} className="border-b">
                <td className="py-2 pr-4">{formatInstant(session.createdAt)}</td>
                <td className="py-2 pr-4">{label(state)}</td>
                <td className="py-2">
                  <Link to="/import/$id" params={{ id: session.id }} className="underline">
                    {ACTIVE_STATES.has(state) ? t('import.resume') : t('import.view')}
                  </Link>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}
