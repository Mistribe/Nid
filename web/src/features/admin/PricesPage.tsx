import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from './format';
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
  const runs = $api.useQuery('get', RUNS);

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
  if (!household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;
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

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.prices.runs_title')}</h2>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.data && !runs.error ? <AdminSkeleton /> : null}
        {runs.data && runs.data.length === 0 ? <p>{t('admin.prices.no_runs')}</p> : null}
        {runs.data && runs.data.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('admin.prices.started')}</TableHead>
                <TableHead>{t('admin.prices.status')}</TableHead>
                <TableHead>{t('admin.prices.queried')}</TableHead>
                <TableHead>{t('admin.prices.accepted')}</TableHead>
                <TableHead>{t('admin.prices.rejected')}</TableHead>
                <TableHead>{t('admin.prices.not_found')}</TableHead>
                <TableHead>{t('admin.prices.source_errors')}</TableHead>
                <TableHead>{t('admin.prices.failure')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {runs.data.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>{formatInstant(r.startedAt)}</TableCell>
                  <TableCell>{statusLabel(r.status)}</TableCell>
                  <TableCell>{r.queried}</TableCell>
                  <TableCell>{r.accepted}</TableCell>
                  <TableCell>{r.rejected}</TableCell>
                  <TableCell>{r.notFound}</TableCell>
                  <TableCell>{r.sourceErrors}</TableCell>
                  <TableCell>{r.failureCode}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </section>
    </AdminFrame>
  );
}
