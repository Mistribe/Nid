import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ALLOCATION_TARGET, DASHBOARD, type Allocation as AllocationData, type AllocationLine, type DashboardView } from '../api';
import { classColor, liquidityColor } from '../colors';
import { percent } from '../format';
import { CLASSES, useClassName, useLiquidityName } from './allocationNames';

function Lines({ lines, kind, withTarget }: { lines: AllocationLine[]; kind: 'class' | 'liquidity'; withTarget: boolean }) {
  const { t } = useTranslation();
  const color = kind === 'class' ? classColor : liquidityColor;
  const className = useClassName();
  const liquidityName = useLiquidityName();
  const name = kind === 'class' ? className : liquidityName;
  return (
    <Table aria-label={t(`dashboard.allocation.table_${kind}`)}>
      <TableHeader>
        <TableRow>
          <TableHead>{t(`dashboard.allocation.col_${kind}`)}</TableHead>
          <TableHead className="text-right">{t('dashboard.allocation.col_amount_share')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((l) => (
          <TableRow key={l.key}>
            <TableHead scope="row" className="whitespace-normal font-normal">
              <span aria-hidden="true" className="mr-2 inline-block size-3 rounded-sm align-middle" style={{ background: color(l.key) }} />
              {name(l.key)}
              {/* The target under the class, so the table keeps two columns on a phone. */}
              {withTarget ? (
                <span className="block pl-5 text-xs text-muted-foreground">
                  {l.targetPct != null && l.gapPct != null && l.gap
                    ? t('dashboard.allocation.target_line', { target: t('dashboard.pct', { value: percent(l.targetPct) }), gap: t('dashboard.allocation.points', { value: percent(l.gapPct, true) }), amount: formatMoney(l.gap) })
                    : t('dashboard.allocation.no_target')}
                </span>
              ) : null}
            </TableHead>
            {/* Two figures per cell, so the table fits a phone without scrolling its target away. */}
            <TableCell className="text-right tabular-nums">
              {formatMoney(l.amount)}
              <span className="block text-xs text-muted-foreground">{t('dashboard.pct', { value: percent(l.pct) })}</span>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** Issue #104: the assets by asset class or by liquidity, the debts apart, the guessed part said, and the household's target. */
export function AllocationSection({ d, kind }: { d: DashboardView; kind: 'class' | 'liquidity' }) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [saved, setSaved] = useState('');
  const a = d.allocation;
  if (!a)
    return (
      <p className="text-sm text-muted-foreground">
        {d.peopleMissingCurrency ? t('dashboard.split.people_unknown_rate', { code: d.peopleMissingCurrency }) : t('dashboard.split.people_none')}
      </p>
    );
  const withTarget = kind === 'class' && Object.keys(a.target).length > 0;
  return (
    <div className="space-y-3">
      <Lines lines={kind === 'class' ? a.classes : a.liquidity} kind={kind} withTarget={withTarget} />
      <ul className="space-y-1 text-sm text-muted-foreground">
        <li>{t('dashboard.allocation.assets', { amount: formatMoney(a.assets) })}</li>
        {a.debts.amount !== '0.00' ? <li>{t('dashboard.allocation.debts', { amount: formatMoney(a.debts) })}</li> : null}
        {kind === 'class' && a.approximate.amount !== '0.00' ? <li>{t('dashboard.allocation.approximate', { amount: formatMoney(a.approximate) })}</li> : null}
      </ul>
      {kind === 'class' ? (
        <Button type="button" variant="outline" size="sm" onClick={() => setEditing(true)}>
          {t(withTarget ? 'dashboard.allocation.edit_target' : 'dashboard.allocation.set_target')}
        </Button>
      ) : null}
      <p role="status" className="text-sm">{saved}</p>
      {editing ? <TargetDialog allocation={a} onClose={() => setEditing(false)} onDone={(message) => { setEditing(false); setSaved(message); }} /> : null}
    </div>
  );
}

function TargetDialog({ allocation, onClose, onDone }: { allocation: AllocationData; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [error, setError] = useState('');
  const save = $api.useMutation('put', ALLOCATION_TARGET, {
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] }),
    onError: (e) => setProblem(present(toProblem(e))),
  });
  const had = Object.keys(allocation.target).length > 0;
  const className = useClassName();
  return (
    <FormDialog title={t('dashboard.allocation.target_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const fields = new FormData(event.currentTarget);
          // Shares in %, not amounts: a French keyboard types a comma.
          const target = Object.fromEntries(CLASSES.flatMap((c) => {
            const raw = ((fields.get(c) as string | null) ?? '').trim().replace(',', '.');
            return raw ? [[c, Number(raw)]] : [];
          }));
          const shares = Object.values(target);
          const total = shares.reduce((sum, v) => sum + v, 0);
          if (shares.some((v) => Number.isNaN(v) || v < 0 || v > 100) || Math.abs(total - 100) > 1e-9) {
            setError(t('dashboard.allocation.target_invalid'));
            return;
          }
          setError('');
          save.mutate({ body: { target } }, { onSuccess: () => done(t('dashboard.allocation.target_saved')) });
        }}>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <p className="text-sm text-muted-foreground">{t('dashboard.allocation.target_hint')}</p>
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
          <div className="grid grid-cols-2 gap-3">
            {CLASSES.map((c) => (
              <Field key={c} label={className(c)}>
                {(control) => (
                  <Input {...control} name={c} inputMode="decimal" defaultValue={allocation.target[c] != null ? String(allocation.target[c]).replace('.', ',') : ''} />
                )}
              </Field>
            ))}
          </div>
          <div className="flex flex-wrap justify-end gap-2">
            {had ? (
              <Button type="button" variant="ghost" disabled={save.isPending}
                onClick={() => save.mutate({ body: { target: {} } }, { onSuccess: () => done(t('dashboard.allocation.target_removed')) })}>
                {t('dashboard.allocation.remove_target')}
              </Button>
            ) : null}
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('dashboard.allocation.save_target')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
