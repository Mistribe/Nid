import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { eventLabel } from '@/features/security/eventLabel';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from './format';

/** The events the journal can hold (spec §5.1), in the order of the filter. */
const JOURNAL_EVENTS = [
  'login_refused', 'access_violation', 'person_preregistered', 'person_preregistration_cancelled',
  'admin_granted', 'admin_revoked', 'session_revoked', 'household_settings_changed',
] as const;

/** screens.md §12, Journal de sécurité: identifiers only, adults by name. */
export function SecurityJournalPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [event, setEvent] = useState<string>('');
  const journal = $api.useQuery('get', '/api/v1/admin/security-journal', { params: { query: event ? { event } : {} } });
  const adults = $api.useQuery('get', '/api/v1/admin/users');

  const title = t('admin.security.title');
  const error = journal.error ?? adults.error;
  if (error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(error))} /></AdminFrame>;

  const name = (id: string | null | undefined) =>
    id ? (adults.data?.find((adult) => adult.id === id)?.displayName ?? id) : null;

  return (
    <AdminFrame title={title}>
      <label className="flex items-center gap-2">
        <span>{t('admin.security.filter')}</span>
        <select value={event} onChange={(e) => setEvent(e.target.value)} className="min-h-11 rounded-md border px-2">
          <option value="">{t('admin.security.all')}</option>
          {JOURNAL_EVENTS.map((name) => <option key={name} value={name}>{eventLabel(t, name)}</option>)}
        </select>
      </label>
      {!journal.data || !adults.data ? <AdminSkeleton /> : journal.data.entries.length === 0 ? (
        <EmptyState title={t('admin.security.empty')} body="" />
      ) : (
        <table className="w-full text-left">
          <thead>
            <tr className="border-b">
              <th scope="col" className="py-2 pr-4">{t('admin.security.when')}</th>
              <th scope="col" className="py-2 pr-4">{t('admin.security.event')}</th>
              <th scope="col" className="py-2 pr-4">{t('admin.security.subject')}</th>
              <th scope="col" className="py-2 pr-4">{t('admin.security.actor')}</th>
              <th scope="col" className="py-2">{t('admin.security.details')}</th>
            </tr>
          </thead>
          <tbody>
            {journal.data.entries.map((entry) => (
              <tr key={entry.seq} className="border-b">
                <td className="py-2 pr-4">{formatInstant(entry.at)}</td>
                <td className="py-2 pr-4">{eventLabel(t, entry.event)}</td>
                <td className="py-2 pr-4">{name(entry.subjectPersonId)}</td>
                <td className="py-2 pr-4">{entry.actor.personId ? name(entry.actor.personId) : t('admin.security.system')}</td>
                <td className="py-2 font-mono">{Object.entries(entry.details).map(([k, v]) => `${k}: ${v}`).join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </AdminFrame>
  );
}
