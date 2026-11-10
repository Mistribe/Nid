import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Loading, SectionSkeleton, TableSkeleton } from '@/components/page/skeletons';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { AdminFrame } from './AdminFrame';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';

const HOUSEHOLD = '/api/v1/admin/household';
const RUNS = '/api/v1/investments/prices/runs';

const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;

/** Administration › Cours: the nightly price collection on or off, a collection now, and its journal. */
export function PricesPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const household = $api.useQuery('get', HOUSEHOLD);
  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } });
  useStepBack(page, runs.data, toPage);

  const fail = (error: unknown) => {
    setProblem(present(toProblem(error)));
  };
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.prices.saved'));
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
    },
    onError: fail,
  });
  const sync = $api.useMutation('post', '/api/v1/investments/prices/sync', {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.prices.sync_requested'));
      void queryClient.invalidateQueries({ queryKey: ['get', RUNS] });
    },
    onError: (error) => {
      // Not a failure to explain: the update the admin asked for is already under way.
      if (toProblem(error).code === 'investments.price_sync_running') {
        setProblem(null);
        toast.info(t('admin.prices.already_running'));
      } else fail(error);
    },
  });
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);

  const title = t('admin.prices.title');
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><SectionSkeleton /></Loading></AdminFrame>;
  const enabled = household.data.pricesEnabled;

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="space-y-3">
        <SwitchField label={t('admin.prices.enabled')} checked={enabled} onCheckedChange={(pricesEnabled) => { save.mutate({ body: { pricesEnabled } }); }} />
        <p className="max-w-prose text-sm text-muted-foreground">{t('admin.prices.hint')}</p>
        <ConfirmAction
          label={t('admin.prices.sync')}
          confirmation={t('admin.prices.sync_confirm')}
          disabled={!enabled}
          pending={sync.isPending}
          onConfirm={() => sync.mutate({})}
        />
      </section>

      <Section title={t('admin.prices.runs_title')}>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.error && (!runs.data || (runs.data.items.length === 0 && runs.data.total > 0)) ? <Loading label={t('admin.loading')}><TableSkeleton columns={8} /></Loading> : null}
        {runs.data && runs.data.total === 0 ? <EmptyState module="/admin" title={t('admin.prices.no_runs')} body="" level={3} /> : null}
        {runs.data && runs.data.items.length > 0 ? (
          <>
            <SortableTable caption={t('admin.prices.runs_title')} rows={runs.data.items} rowKey={(r) => r.id}
              columns={[
                { id: 'started', header: t('admin.prices.started'), cell: (r) => formatInstant(r.startedAt) },
                { id: 'status', header: t('admin.prices.status'), cell: (r) => statusLabel(r.status) },
                { id: 'queried', header: t('admin.prices.queried'), cell: (r) => r.queried },
                { id: 'accepted', header: t('admin.prices.accepted'), cell: (r) => r.accepted },
                { id: 'rejected', header: t('admin.prices.rejected'), cell: (r) => r.rejected },
                { id: 'not_found', header: t('admin.prices.not_found'), cell: (r) => r.notFound },
                { id: 'source_errors', header: t('admin.prices.source_errors'), cell: (r) => r.sourceErrors },
                { id: 'failure', header: t('admin.prices.failure'), cell: (r) => r.failureCode },
              ]}
              mobile={(r) => (
                <>
                  <p className="font-medium">{formatInstant(r.startedAt)} · {statusLabel(r.status)}</p>
                  <p className="text-sm text-muted-foreground">
                    {t('admin.prices.queried')} {r.queried} · {t('admin.prices.accepted')} {r.accepted} · {t('admin.prices.rejected')} {r.rejected} · {t('admin.prices.not_found')} {r.notFound} · {t('admin.prices.source_errors')} {r.sourceErrors}{r.failureCode ? ` · ${r.failureCode}` : ''}
                  </p>
                </>
              )} />
            <Pager page={page} total={runs.data.total} onPage={toPage} />
          </>
        ) : null}
      </Section>
    </AdminFrame>
  );
}
