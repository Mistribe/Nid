import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from './format';
import '@/locales/admin';

const HOUSEHOLD = '/api/v1/admin/household';
const RUNS = '/api/v1/admin/banking/runs';

const STATUS = { running: 'admin.fx.status_running', succeeded: 'admin.fx.status_succeeded', failed: 'admin.fx.status_failed' } as const;
/** « Conserver les opérations » (D3): the choices offered, in months; « Sans limite » clears the setting. */
const RETENTION = [12, 24, 36, 60, 120];
const TRIGGER = { schedule: 'admin.banking.trigger_schedule', manual: 'admin.banking.trigger_manual' } as const;

/** Administration › Banque: bank sync on or off, the Enable Banking application, how long the operations are kept, and the journal of the syncs (counts only). */
export function BankingPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [appId, setAppId] = useState<string | null>(null);
  const household = $api.useQuery('get', HOUSEHOLD);
  const runs = $api.useQuery('get', RUNS);
  const save = $api.useMutation('patch', HOUSEHOLD, {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.banking.saved'));
      setAppId(null);
      void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD] });
      // The settings tabs and the envelope pages ask whether bank sync is on.
      void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/banking/status'] });
    },
    onError: (error) => { setProblem(present(toProblem(error))); },
  });
  // The wire carries plain strings; an unknown value is shown as it came rather than hidden.
  const outcomeLabel = (outcome: string) => (outcome in STATUS ? t(STATUS[outcome as keyof typeof STATUS]) : outcome);
  const triggerLabel = (trigger: string) => (trigger in TRIGGER ? t(TRIGGER[trigger as keyof typeof TRIGGER]) : trigger);

  const title = t('admin.banking.title');
  const retention = household.data?.bankRetentionMonths;
  if (household.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(household.error))} /></AdminFrame>;
  if (!household.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;

  return (
    <AdminFrame title={title}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="max-w-prose space-y-3">
        <SwitchField label={t('admin.banking.enabled')} checked={household.data.bankingEnabled}
          onCheckedChange={(bankingEnabled) => { save.mutate({ body: { bankingEnabled } }); }} />
        <p className="text-sm text-muted-foreground">{t('admin.banking.hint')}</p>
        <p className="text-sm">{t('admin.banking.provider', { publicUrl: window.location.origin })}</p>
        <form noValidate className="flex flex-wrap items-end gap-2" onSubmit={(event) => {
          event.preventDefault();
          save.mutate({ body: { enableBankingAppId: appId ?? household.data.enableBankingAppId ?? '' } });
        }}>
          <div className="min-w-64 flex-1">
            <Field label={t('admin.banking.app_id')} hint={t('admin.banking.app_id_hint')}>
              {(c) => <Input {...c} value={appId ?? household.data.enableBankingAppId ?? ''} onChange={(e) => setAppId(e.target.value)} />}
            </Field>
          </div>
          <Button type="submit" variant="outline" disabled={save.isPending}>{t('admin.banking.save_app_id')}</Button>
        </form>
        <div className="max-w-64">
          <Field label={t('admin.banking.retention')} hint={t('admin.banking.retention_hint')}>
            {(c) => (
              <SelectField {...c} value={retention?.toString() ?? ''} disabled={save.isPending}
                onValueChange={(v) => { save.mutate({ body: v ? { bankRetentionMonths: Number(v) } : { clearBankRetention: true } }); }}
                options={[
                  { value: '', label: t('admin.banking.retention_none') },
                  // A value set through the API outside these choices is shown as it is.
                  ...[...RETENTION, ...(retention && !RETENTION.includes(retention) ? [retention] : [])].sort((a, b) => a - b)
                    .map((months) => ({ value: String(months), label: t('admin.banking.retention_months', { months }) })),
                ]} />
            )}
          </Field>
        </div>
      </section>

      <section className="space-y-2">
        <h2 className="text-lg font-semibold">{t('admin.banking.runs_title')}</h2>
        {runs.error ? <ProblemMessage problem={present(toProblem(runs.error))} /> : null}
        {!runs.data && !runs.error ? <AdminSkeleton /> : null}
        {runs.data && runs.data.length === 0 ? <p>{t('admin.banking.no_runs')}</p> : null}
        {runs.data && runs.data.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('admin.banking.started')}</TableHead>
                <TableHead>{t('admin.banking.trigger')}</TableHead>
                <TableHead>{t('admin.banking.outcome')}</TableHead>
                <TableHead>{t('admin.banking.connections')}</TableHead>
                <TableHead>{t('admin.banking.accounts')}</TableHead>
                <TableHead>{t('admin.banking.balances')}</TableHead>
                <TableHead>{t('admin.banking.proposals')}</TableHead>
                <TableHead>{t('admin.banking.written')}</TableHead>
                <TableHead>{t('admin.banking.skipped')}</TableHead>
                <TableHead>{t('admin.banking.failure')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {runs.data.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>{formatInstant(r.startedAt)}</TableCell>
                  <TableCell>{triggerLabel(r.trigger)}</TableCell>
                  <TableCell>{outcomeLabel(r.outcome)}</TableCell>
                  <TableCell>{r.connections}</TableCell>
                  <TableCell>{r.accounts}</TableCell>
                  <TableCell>{r.balances}</TableCell>
                  <TableCell>{r.proposals}</TableCell>
                  <TableCell>{r.written}</TableCell>
                  <TableCell>{r.skipped}</TableCell>
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
