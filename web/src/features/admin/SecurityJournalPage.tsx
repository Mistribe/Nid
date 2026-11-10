import { SelectField } from '@/components/form/SelectField';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { EmptyState } from '@/components/EmptyState';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { SortableTable } from '@/components/page/SortableTable';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { ProblemMessage } from '@/components/ProblemMessage';
import { eventLabel } from '@/features/security/eventLabel';
import { AdminFrame } from './AdminFrame';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';

/** The events the journal can hold (spec §5.1), in the order of the filter. */
const JOURNAL_EVENTS = [
  'login_refused', 'access_violation', 'person_preregistered', 'person_preregistration_cancelled',
  'admin_granted', 'admin_revoked', 'session_revoked', 'household_settings_changed', 'admin_command', 'subject_rebound',
] as const;

type JournalEntry = components['schemas']['SecurityEventView'];

const rawDetails = (details: JournalEntry['details']) => Object.entries(details).map(([k, v]) => `${k}: ${v}`).join(', ');

const REASONS = new Set(['no_actor', 'visibility_lookup_failed', 'outside_visible_set']);
const ITEM_KINDS = new Set(['envelope', 'loan', 'property', 'scenario', 'source', 'subscription']);

/** #28: why the response was withheld, in words; the raw identifiers stay one click away. */
function ViolationDetails({ entry }: { entry: JournalEntry }) {
  const { t } = useTranslation();
  const { reason = '' } = entry.details;
  const items = entry.items.map((item) => {
    if (!item.name) return item.id;
    const kind = item.kind && ITEM_KINDS.has(item.kind) ? t(`admin.security.item_kind.${item.kind}` as 'admin.security.item_kind.loan') : item.kind;
    return kind ? `${item.name} (${kind})` : item.name;
  });
  return (
    <Collapsible className="space-y-1 font-sans">
      <p>{REASONS.has(reason) ? t(`admin.security.reason.${reason}` as 'admin.security.reason.no_actor') : reason}</p>
      {items.length > 0 && <p>{items.join(', ')}</p>}
      <CollapsibleTrigger className="text-muted-foreground underline">{t('admin.security.raw')}</CollapsibleTrigger>
      <CollapsibleContent className="font-mono">{rawDetails(entry.details)}</CollapsibleContent>
    </Collapsible>
  );
}

function Details({ entry }: { entry: JournalEntry }) {
  return (
    <div className="font-mono text-xs break-all">
      {entry.event === 'access_violation' && entry.details.reason ? <ViolationDetails entry={entry} /> : rawDetails(entry.details)}
    </div>
  );
}

/** screens.md §12, Journal de sécurité: identifiers only, adults by name; a withheld response says why (#28). */
export function SecurityJournalPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const { event = '' }: { event?: string } = useSearch({ strict: false });
  const [page, toPage] = useListPage();
  // A new filter starts again at the first page.
  const setEvent = (value: string) => void navigate({ to: '.', replace: true, search: (prev) => ({ ...prev, event: value || undefined, page: undefined }) });
  const journal = $api.useQuery('get', '/api/v1/admin/security-journal', { params: { query: { ...(event ? { event } : {}), page } } });
  useStepBack(page, journal.data, toPage);
  const adults = $api.useQuery('get', '/api/v1/admin/users');

  const title = t('admin.security.title');
  const error = journal.error ?? adults.error;
  if (error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(error))} /></AdminFrame>;

  const name = (id: string | null | undefined) =>
    id ? (adults.data?.find((adult) => adult.id === id)?.displayName ?? id) : null;

  const actor = (entry: JournalEntry) => (entry.actor.personId ? name(entry.actor.personId) : t('admin.security.system'));

  return (
    <AdminFrame title={title}>
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('admin.security.filter')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-64 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={event} onValueChange={setEvent} options={[
                { value: '', label: t('admin.security.all') },
                ...JOURNAL_EVENTS.map((name) => ({ value: name, label: eventLabel(t, name) })),
              ]} />
            </div>
          )}
        </FilterField>
      </FilterRow>
      {!journal.data || !adults.data ? <Loading label={t('admin.loading')}><TableSkeleton columns={5} /></Loading> : journal.data.total === 0 ? (
        <EmptyState module="/admin" title={t('admin.security.empty')} body="" />
      ) : journal.data.items.length === 0 ? <Loading label={t('admin.loading')}><TableSkeleton columns={5} /></Loading> : (
        <>
          <SortableTable<JournalEntry>
            caption={title}
            rows={journal.data.items}
            rowKey={(entry) => String(entry.seq)}
            columns={[
              { id: 'when', header: t('admin.security.when'), cell: (entry) => formatInstant(entry.at) },
              { id: 'event', header: t('admin.security.event'), cell: (entry) => eventLabel(t, entry.event) },
              { id: 'subject', header: t('admin.security.subject'), cell: (entry) => name(entry.subjectPersonId) },
              { id: 'actor', header: t('admin.security.actor'), cell: (entry) => actor(entry) },
              { id: 'details', header: t('admin.security.details'), wrap: true, cell: (entry) => <Details entry={entry} /> },
            ]}
            mobile={(entry) => (
              <div className="space-y-1">
                <div className="flex items-center justify-between gap-2"><span>{eventLabel(t, entry.event)}</span><span className="text-sm text-muted-foreground">{formatInstant(entry.at)}</span></div>
                <p className="text-sm text-muted-foreground">{[name(entry.subjectPersonId), actor(entry)].filter(Boolean).join(' · ')}</p>
                <Details entry={entry} />
              </div>
            )}
          />
          <Pager page={page} total={journal.data.total} onPage={toPage} />
        </>
      )}
    </AdminFrame>
  );
}
