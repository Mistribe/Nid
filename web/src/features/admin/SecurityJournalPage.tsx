import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { eventLabel } from '@/features/security/eventLabel';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from './format';
import '@/locales/admin';

/** The events the journal can hold (spec §5.1), in the order of the filter. */
const JOURNAL_EVENTS = [
  'login_refused', 'access_violation', 'person_preregistered', 'person_preregistration_cancelled',
  'admin_granted', 'admin_revoked', 'session_revoked', 'household_settings_changed', 'admin_command', 'subject_rebound',
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
      <div className="max-w-xs">
        <Field label={t('admin.security.filter')}>
          {(c) => (
            <SelectField {...c} value={event} onValueChange={setEvent} options={[
              { value: '', label: t('admin.security.all') },
              ...JOURNAL_EVENTS.map((name) => ({ value: name, label: eventLabel(t, name) })),
            ]} />
          )}
        </Field>
      </div>
      {!journal.data || !adults.data ? <AdminSkeleton /> : journal.data.entries.length === 0 ? (
        <EmptyState title={t('admin.security.empty')} body="" />
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t('admin.security.when')}</TableHead>
              <TableHead>{t('admin.security.event')}</TableHead>
              <TableHead>{t('admin.security.subject')}</TableHead>
              <TableHead>{t('admin.security.actor')}</TableHead>
              <TableHead>{t('admin.security.details')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {journal.data.entries.map((entry) => (
              <TableRow key={entry.seq}>
                <TableCell>{formatInstant(entry.at)}</TableCell>
                <TableCell>{eventLabel(t, entry.event)}</TableCell>
                <TableCell>{name(entry.subjectPersonId)}</TableCell>
                <TableCell>{entry.actor.personId ? name(entry.actor.personId) : t('admin.security.system')}</TableCell>
                <TableCell className="font-mono">{Object.entries(entry.details).map(([k, v]) => `${k}: ${v}`).join(', ')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </AdminFrame>
  );
}
