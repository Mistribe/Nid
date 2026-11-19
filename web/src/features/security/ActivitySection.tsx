import { useNavigate, useSearch } from '@tanstack/react-router';
import { TriangleAlertIcon } from 'lucide-react';
import type { TFunction } from 'i18next';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { Segmented } from '@/components/form/Segmented';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { formatInstant } from '@/lib/format';
import { eventLabel, viaLabel } from './eventLabel';
import '@/locales/security';

type Entry = components['schemas']['SecurityEventView'];
type SecurityEventPage = components['schemas']['SecurityEventPage'];

/** What the filter offers; `access` (everything but the assistant's reads) is the default and stays out of the URL. */
export const EVENT_KINDS = ['access', 'assistant', 'all'] as const;
export type EventKind = (typeof EVENT_KINDS)[number];

/** A refusal or a failure stands out from the routine. */
const WARNINGS = new Set(['access_denied', 'access_violation', 'login_failed', 'login_locked', 'login_refused', 'token_auth_failed']);

/** What each event may show (`SecurityEventViews.Shown`), in words, then where it came from. */
function details(t: TFunction, entry: Entry): string {
  const { tool, preset, access, prefix } = entry.details;
  const said = entry.event === 'mcp_tool_called' && tool ? t('security_page.activity.tool', { tool })
    : entry.event === 'token_created' && preset ? t('security_page.activity.preset', { preset: t(`security.tokens.preset.${preset as 'read'}`) })
    : entry.event === 'access_denied' && access ? t('security_page.activity.access', { access })
    : entry.event === 'token_auth_failed' && prefix ? t('security_page.activity.prefix', { prefix })
    : null;
  return [said, viaLabel(t, entry.actor.via)].filter(Boolean).join(' · ');
}

function Event({ entry }: { entry: Entry }) {
  const { t } = useTranslation();
  return WARNINGS.has(entry.event) ? (
    <span className="inline-flex items-center gap-1 font-medium text-status-warning-text">
      <TriangleAlertIcon aria-hidden="true" className="size-4 flex-none" />{eventLabel(t, entry.event)}
    </span>
  ) : <span>{eventLabel(t, entry.event)}</span>;
}

/** #308, the Activité tab: my security events, filtered on the server, twenty at a time through the `before` cursor. */
export function ActivitySection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const { show }: { show?: EventKind } = useSearch({ strict: false });
  const kind = show ?? 'access';
  const filterId = useId();
  const setKind = (next: string) => void navigate({ to: '.', replace: true, resetScroll: false, search: (prev: Record<string, unknown>) => ({ ...prev, show: next === 'access' ? undefined : next }) as never });
  const events = $api.useInfiniteQuery('get', '/api/v1/platform/audit/me/security', { params: { query: { kind, limit: 20 } } }, {
    pageParamName: 'before',
    // null is left out of the query: the first page has no cursor.
    initialPageParam: null as unknown as number,
    getNextPageParam: (last: SecurityEventPage) => last.next ?? undefined,
  });
  const rows = events.data?.pages.flatMap((page) => page.entries) ?? [];

  return (
    <section aria-labelledby={filterId} className="space-y-4">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h2 id={filterId} className="text-title">{t('security.events_title')}</h2>
        <Segmented aria-label={t('security_page.activity.filter')} value={kind} onValueChange={setKind}
          options={EVENT_KINDS.map((k) => ({ value: k, label: t(`security_page.activity.kind.${k}`) }))} />
      </div>
      {events.error ? <ProblemMessage problem={present(toProblem(events.error))} /> : null}
      {events.data && rows.length === 0 ? <p>{t('security.empty')}</p> : null}
      {rows.length > 0 ? (
        <SortableTable<Entry> caption={t('security.events_title')} rows={rows} rowKey={(entry) => String(entry.seq)}
          columns={[
            { id: 'when', header: t('security.when'), cell: (entry) => formatInstant(entry.at) },
            { id: 'event', header: t('security.event'), cell: (entry) => <Event entry={entry} /> },
            { id: 'details', header: t('security_page.activity.details'), wrap: true, cell: (entry) => <span className="text-muted-foreground">{details(t, entry)}</span> },
          ]}
          mobile={(entry) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2"><Event entry={entry} /><span className="flex-none text-sm text-muted-foreground">{formatInstant(entry.at)}</span></div>
              <p className="text-sm break-words text-muted-foreground">{details(t, entry)}</p>
            </div>
          )} />
      ) : null}
      {events.hasNextPage ? (
        <Button type="button" variant="outline" disabled={events.isFetchingNextPage} onClick={() => void events.fetchNextPage()}>{t('security_page.activity.more')}</Button>
      ) : null}
    </section>
  );
}
