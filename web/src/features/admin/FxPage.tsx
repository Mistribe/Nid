import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { ConfirmAction } from '@/components/ConfirmAction';
import { CURRENCY } from '@/lib/amount';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';
import '@/locales/forms';
import '@/locales/series';

const RUNS = '/api/v1/core/fx-rates/runs';

const KIND = { daily: 'admin.fx.kind_daily', backfill: 'admin.fx.kind_backfill' } as const;
const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;

/** screens.md §12, Taux de change: manual entry, on-demand collection, and the collection journal. */
export function FxPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [entering, setEntering] = useState(false);
  const runs = $api.useQuery('get', RUNS);

  const fail = (error: unknown) => {
    setProblem(present(toProblem(error)));
  };
  const sync = $api.useMutation('post', '/api/v1/core/fx-rates/sync', {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.fx.sync_requested'));
      void queryClient.invalidateQueries({ queryKey: ['get', RUNS] });
    },
    onError: (error) => {
      // Not a failure to explain: the collection the admin asked for is already under way.
      if (toProblem(error).code === 'core.fx_sync_running') {
        setProblem(null);
        toast.info(t('admin.fx.already_running'));
      } else fail(error);
    },
  });
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const kindLabel = (kind: string) => (kind in KIND ? t(KIND[kind as keyof typeof KIND]) : kind);
  const statusLabel = (status: string) => (status in STATUS ? t(STATUS[status as keyof typeof STATUS]) : status);

  return (
    <AdminFrame title={t('admin.fx.title')}>
      {problem ? <ProblemMessage problem={problem} /> : null}

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

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.fx.manual_title')}</h2>
        <Button variant="outline" onClick={() => { setEntering(true); }}>{t('admin.fx.manual_title')}</Button>
      </section>
      {entering ? <ManualRateDialog onClose={() => setEntering(false)} onDone={(message) => { setEntering(false); setProblem(null); toast.success(message); }} /> : null}

      <SeriesByCurrency />

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.fx.runs_title')}</h2>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.data && !runs.error ? <AdminSkeleton /> : null}
        {runs.data && runs.data.length === 0 ? <p>{t('admin.fx.no_runs')}</p> : null}
        {runs.data && runs.data.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('admin.fx.started')}</TableHead>
                <TableHead>{t('admin.fx.kind')}</TableHead>
                <TableHead>{t('admin.fx.status')}</TableHead>
                <TableHead>{t('admin.fx.rows')}</TableHead>
                <TableHead>{t('admin.fx.failure')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {runs.data.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>{formatInstant(r.startedAt)}</TableCell>
                  <TableCell>{kindLabel(r.kind)}</TableCell>
                  <TableCell>{statusLabel(r.status)}</TableCell>
                  <TableCell>{r.rowsWritten}</TableCell>
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

/** « Historique d'une devise »: the stored quotes are every currency SubTracker gives, so the code is typed, not picked. */
function SeriesByCurrency() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [error, setError] = useState('');
  return (
    <section className="space-y-2">
      <h2 className="text-lg font-semibold">{t('admin.fx.series_title')}</h2>
      <form aria-label={t('admin.fx.series_title')} noValidate className="flex max-w-md items-start gap-2" onSubmit={(event) => {
        event.preventDefault();
        const code = ((new FormData(event.currentTarget).get('quote') as string | null) ?? '').trim().toUpperCase();
        if (!CURRENCY.test(code) || code === 'USD') { setError(t('admin.fx.series_invalid')); return; }
        setError('');
        void navigate({ to: '/series', search: { s: `fx:${code}` } });
      }}>
        <Field label={t('admin.fx.series_quote')} error={error}>{(c) => <Input {...c} name="quote" onChange={() => setError('')} maxLength={3} className="uppercase" />}</Field>
        <Button type="submit" className="mt-6">{t('series.see')}</Button>
      </form>
    </section>
  );
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** « Saisir un taux »: one rate for one day, in a dialog; the page toasts the outcome. */
function ManualRateDialog({ onClose, onDone }: { onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [date, setDate] = useState('');
  const [errors, setErrors] = useState({ date: '', quote: '', rate: '' });
  const manual = $api.useMutation('put', '/api/v1/core/fx-rates/{date}/{quote}');
  const problem = manual.error ? present(toProblem(manual.error)) : null;

  return (
    <FormDialog title={t('admin.fx.manual_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const fields = new FormData(event.currentTarget);
          const quote = ((fields.get('quote') as string | null) ?? '').trim().toUpperCase();
          // A French keyboard types a comma; the wire wants a decimal point (rates are decimal strings, like amounts).
          const rate = ((fields.get('rate') as string | null) ?? '').trim().replace(',', '.');
          const errors = {
            date: ISO_DATE.test(date) ? '' : t('admin.fx.date_invalid'),
            quote: quote ? '' : t('edit.required'),
            rate: rate ? '' : t('edit.required'),
          };
          setErrors(errors);
          if (errors.date || errors.quote || errors.rate) return;
          manual.mutate({ params: { path: { date, quote } }, body: { rate } }, { onSuccess: () => done(t('admin.fx.rate_saved')) });
        }}>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <Field label={t('admin.fx.date')} error={errors.date}>{(c) => <DateField {...c} value={date} onChange={setDate} />}</Field>
          <Field label={t('admin.fx.quote')} error={errors.quote}>{(c) => <Input {...c} name="quote" required maxLength={3} className="uppercase" />}</Field>
          <Field label={t('admin.fx.rate')} error={errors.rate}>{(c) => <Input {...c} name="rate" required inputMode="decimal" />}</Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={manual.isPending}>{t('admin.fx.save_rate')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
