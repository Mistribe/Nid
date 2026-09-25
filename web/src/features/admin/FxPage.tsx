import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { ConfirmAction } from '@/components/ConfirmAction';
import { formatInstant } from './format';

const RUNS = '/api/v1/core/fx-rates/runs';

const KIND = { daily: 'admin.fx.kind_daily', backfill: 'admin.fx.kind_backfill' } as const;
const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;
const NOTICE = {
  sync_requested: 'admin.fx.sync_requested',
  already_running: 'admin.fx.already_running',
  rate_saved: 'admin.fx.rate_saved',
} as const;

/** screens.md §12, Taux de change: manual entry, on-demand collection, and the collection journal. */
export function FxPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [notice, setNotice] = useState<keyof typeof NOTICE | null>(null);
  const runs = $api.useQuery('get', RUNS);

  const fail = (error: unknown) => {
    setNotice(null);
    setProblem(present(toProblem(error)));
  };
  const sync = $api.useMutation('post', '/api/v1/core/fx-rates/sync', {
    onSuccess: () => {
      setProblem(null);
      setNotice('sync_requested');
      void queryClient.invalidateQueries({ queryKey: ['get', RUNS] });
    },
    onError: (error) => {
      // Not a failure to explain: the collection the admin asked for is already under way.
      if (toProblem(error).code === 'core.fx_sync_running') {
        setProblem(null);
        setNotice('already_running');
      } else fail(error);
    },
  });
  const manual = $api.useMutation('put', '/api/v1/core/fx-rates/{date}/{quote}', {
    onSuccess: () => {
      setProblem(null);
      setNotice('rate_saved');
    },
    onError: fail,
  });

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = new FormData(form);
    const date = (fields.get('date') as string | null) ?? '';
    const quote = ((fields.get('quote') as string | null) ?? '').trim().toUpperCase();
    // A French keyboard types a comma; the wire wants a decimal point (rates are decimal strings, like amounts).
    const rate = ((fields.get('rate') as string | null) ?? '').trim().replace(',', '.');
    manual.mutate({ params: { path: { date, quote } }, body: { rate } }, { onSuccess: () => form.reset() });
  };

  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const kindLabel = (kind: string) => (kind in KIND ? t(KIND[kind as keyof typeof KIND]) : kind);
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);

  return (
    <AdminFrame title={t('admin.fx.title')}>
      {problem ? <ProblemMessage problem={problem} /> : null}
      {notice ? <p role="status">{t(NOTICE[notice])}</p> : null}

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.fx.sync_title')}</h2>
        <p className="text-sm text-muted-foreground">{t('admin.fx.sync_hint')}</p>
        <ConfirmAction
          label={t('admin.fx.sync')}
          confirmation={t('admin.fx.sync_confirm')}
          pending={sync.isPending}
          onConfirm={() => sync.mutate({})}
        />
      </section>

      <form onSubmit={submit} className="max-w-md space-y-3">
        <h2 className="text-lg font-semibold">{t('admin.fx.manual_title')}</h2>
        <label className="block">
          <span className="block text-sm">{t('admin.fx.date')}</span>
          <input name="date" type="date" required className="w-full rounded-md border px-3 py-2" />
        </label>
        <label className="block">
          <span className="block text-sm">{t('admin.fx.quote')}</span>
          <input name="quote" required maxLength={3} className="w-full rounded-md border px-3 py-2 uppercase" />
        </label>
        <label className="block">
          <span className="block text-sm">{t('admin.fx.rate')}</span>
          <input name="rate" required inputMode="decimal" className="w-full rounded-md border px-3 py-2" />
        </label>
        <Button type="submit" disabled={manual.isPending}>{t('admin.fx.save_rate')}</Button>
      </form>

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.fx.runs_title')}</h2>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.data && !runs.error ? <AdminSkeleton /> : null}
        {runs.data && runs.data.length === 0 ? <p>{t('admin.fx.no_runs')}</p> : null}
        {runs.data && runs.data.length > 0 ? (
          <table className="w-full text-left">
            <thead>
              <tr className="border-b">
                <th scope="col" className="py-2 pr-4">{t('admin.fx.started')}</th>
                <th scope="col" className="py-2 pr-4">{t('admin.fx.kind')}</th>
                <th scope="col" className="py-2 pr-4">{t('admin.fx.status')}</th>
                <th scope="col" className="py-2 pr-4">{t('admin.fx.rows')}</th>
                <th scope="col" className="py-2">{t('admin.fx.failure')}</th>
              </tr>
            </thead>
            <tbody>
              {runs.data.map((r) => (
                <tr key={r.id} className="border-b">
                  <td className="py-2 pr-4">{formatInstant(r.startedAt)}</td>
                  <td className="py-2 pr-4">{kindLabel(r.kind)}</td>
                  <td className="py-2 pr-4">{statusLabel(r.status)}</td>
                  <td className="py-2 pr-4">{r.rowsWritten}</td>
                  <td className="py-2">{r.failureCode}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </section>
    </AdminFrame>
  );
}
