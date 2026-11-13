import { useQueryClient } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { elision } from '@/lib/elision';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Field } from '@/components/form/Field';
import { formatCurrency, Money } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail, type Crumb } from '@/components/page/trail';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  ARRANGEMENT, dayBody, type ChildcareDetail, useDiscardStatement, useReopenStatement, useSaveStatement, useStatement, useValidateStatement, type Statement, type StatementDay,
  type StatementDayBody,
} from './api';
import { DayDialog } from './DayDialog';
import { Declaration, type DeclarationEntry } from './Declaration';
import { MonthSummaryDialog, type MonthSummary } from './MonthSummaryDialog';
import { dayTitle, frenchNumber, monthTitle, problemText, reasonLabel, stateLabel, statementError } from './statementText';
import '@/locales/childcare';
import { PageSkeleton } from '@/components/page/skeletons';

/** An amount typed the French way, sent as the decimal string the server reads; `null` when it is not one. */
const amountBody = (text: string): string | null => {
  const value = text.trim().replace(/\s/g, '').replace(',', '.');
  return /^\d+(\.\d{1,2})?$/.test(value) ? value : null;
};

const COUNTS = ['normalHours', 'activityDays', 'paidLeaveDays'] as const;
const AMOUNTS = ['complementaryHours', 'overtimeHours', 'netPay', 'upkeep', 'meals'] as const;
type Entered = Record<(typeof COUNTS)[number] | (typeof AMOUNTS)[number], string>;

const countBody = (text: string): number | null => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : null);
const optional = <T,>(text: string, parse: (text: string) => T | null) => (text.trim() === '' ? null : parse(text));

/** Spec §5 « Relevé du mois »: the days from the template, their deviations, the month's figures and the Pajemploi declaration. */
export function StatementPage() {
  const { t } = useTranslation();
  const { id, month } = useParams({ from: '/childcare/$id/statements/$month' });
  const { $api } = useApi();
  const answer = useStatement(id, month);
  // Before the month is read, the arrangement is named from the cache when already read (no request), else generically.
  const name = useQueryClient().getQueryData<ChildcareDetail>($api.queryOptions('get', ARRANGEMENT, { params: { path: { id } } }).queryKey)?.summary.name;
  const crumbs: Crumb[] = [{ label: t('childcare_list.title'), to: '/childcare' }, { label: name ?? t('childcare_list.item_fallback'), to: '/childcare/$id', params: { id } }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('childcare_statement.loading')} actions={2} stats={4} /></>;
  return <Month key={`${month}:${answer.data.version}:${answer.data.state}`} id={id} month={month} view={answer.data} />;
}

function Month({ id, month, view }: { id: string; month: string; view: Statement }) {
  const { t } = useTranslation();
  const save = useSaveStatement();
  const validate = useValidateStatement();
  const reopen = useReopenStatement();
  const discard = useDiscardStatement();
  const [editing, setEditing] = useState<number | null>(null);
  const [mileage, setMileage] = useState(frenchNumber(view.mileage));
  const [cmg, setCmg] = useState(view.cmg == null ? '' : frenchNumber(view.cmg));
  const [contributions, setContributions] = useState(view.contributions == null ? '' : frenchNumber(view.contributions));
  const [exemption, setExemption] = useState(view.exemption == null ? '' : frenchNumber(view.exemption));
  const [entered, setEntered] = useState<Entered>(() => {
    const d = view.declared;
    return {
      normalHours: String(d?.normalHours ?? ''), activityDays: String(d?.activityDays ?? ''), paidLeaveDays: String(d?.paidLeaveDays ?? ''),
      complementaryHours: frenchNumber(d?.complementaryHours ?? ''), overtimeHours: frenchNumber(d?.overtimeHours ?? ''),
      netPay: frenchNumber(d?.netPay ?? ''), upkeep: frenchNumber(d?.upkeep ?? ''), meals: frenchNumber(d?.meals ?? ''),
    };
  });
  const [summarising, setSummarising] = useState(false);
  const [error, setError] = useState('');
  const path = { params: { path: { id, month } } };
  const editable = view.canWrite && view.state !== 'validated';
  const failed = (e: unknown) => setError(statementError(t, e));

  const send = (days: StatementDayBody[], then?: () => void, summary?: MonthSummary) => {
    const m = amountBody(mileage);
    const c = optional(cmg, amountBody);
    const k = optional(contributions, amountBody);
    const x = optional(exemption, amountBody);
    const wellFormed = COUNTS.every((k) => entered[k].trim() === '' || countBody(entered[k]) !== null)
      && AMOUNTS.every((k) => entered[k].trim() === '' || amountBody(entered[k]) !== null);
    if (m === null || (cmg.trim() !== '' && c === null) || (contributions.trim() !== '' && k === null) || (exemption.trim() !== '' && x === null) || !wellFormed) { setError(t('childcare_statement.error.amount_format')); return; }
    const declared = {
      normalHours: optional(entered.normalHours, countBody), activityDays: optional(entered.activityDays, countBody), paidLeaveDays: optional(entered.paidLeaveDays, countBody),
      complementaryHours: optional(entered.complementaryHours, amountBody), overtimeHours: optional(entered.overtimeHours, amountBody),
      netPay: optional(entered.netPay, amountBody), upkeep: optional(entered.upkeep, amountBody), meals: optional(entered.meals, amountBody),
    };
    setError('');
    save.mutate({ ...path, body: { days, mileage: m, cmg: c, contributions: k, exemption: x, version: view.version, declared, summary } },
      { onSuccess: () => { toast.success(t('childcare_statement.saved')); then?.(); }, onError: failed });
  };
  const days = view.days.map(dayBody);
  const problemsOf = (i: number) => view.problems.filter((p) => p.field.startsWith(`days[${i}]`));

  const entry: DeclarationEntry | null = editable ? {
    values: { ...entered, mileage }, pending: save.isPending, onSave: () => send(days),
    onChange: (key, value) => { if (key === 'mileage') setMileage(value); else setEntered((e) => ({ ...e, [key]: value })); },
  } : null;

  const actions = !view.canWrite ? null : (
    <>
      {view.state === 'validated' ? (
        view.canReopen ? (
          <Button type="button" variant="outline" disabled={reopen.isPending}
            onClick={() => reopen.mutate(path, { onSuccess: () => toast.success(t('childcare_statement.reopened')), onError: failed })}>
            {t('childcare_statement.reopen')}
          </Button>
        ) : null
      ) : (
        <>
          {view.state === 'draft' ? (
            <ConfirmAction label={t('childcare_statement.discard')} confirmation={t('childcare_statement.discard_confirm')} pending={discard.isPending}
              onConfirm={() => discard.mutate(path, { onSuccess: () => toast.success(t('childcare_statement.discarded')), onError: failed })} />
          ) : null}
          <ConfirmAction label={t('childcare_statement.validate')} confirmation={t('childcare_statement.validate_confirm', { month: monthTitle(month), context: elision(monthTitle(month)) })}
            disabled={view.figures == null || view.cmg == null} pending={validate.isPending}
            onConfirm={() => validate.mutate({ ...path, body: { version: view.version } },
              { onSuccess: () => toast.success(t('childcare_statement.validated')), onError: failed })} />
        </>
      )}
    </>
  );

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[...[{ label: t('childcare_list.title'), to: '/childcare' as const }], { label: view.name, to: '/childcare/$id', params: { id } }]}
        title={monthTitle(month)} actions={actions}
        meta={<Badge variant={view.state === 'validated' ? 'default' : 'secondary'}>{stateLabel(t, view.state)}</Badge>}>
        <section aria-label={t('childcare_statement.figures')}>
          {view.figures ? (
            <StatLine items={[
              { label: t('childcare_statement.net_pay'), value: formatCurrency(view.figures.declaration.netPay, 'EUR'),
                hint: view.declared?.netPay == null ? null : t('childcare_statement.entered_by_hand') },
              { label: t('childcare_statement.cost'), value: <Money value={view.figures.cost} tabular />,
                hint: view.figures.costBeforeCmg ? t('childcare_statement.before_cmg') : null },
              { label: t('childcare_statement.cmg'), tip: t('childcare_detail.cmg_tip'), value: view.cmg == null ? '—' : formatCurrency(view.cmg, 'EUR') },
              ...(view.contributions == null ? [] : [{ label: t('childcare_statement.contributions'), value: formatCurrency(view.contributions, 'EUR') }]),
              ...(!view.figures.exemption || /^0(\.0+)?$/.test(view.figures.exemption.amount) ? [] : [{ label: t('childcare_statement.exemption'),
                tip: t('childcare_statement.exemption_tip'), value: <Money value={view.figures.exemption} tabular />,
                hint: view.exemption == null ? t('childcare_statement.estimated') : null }]),
            ]} />
          ) : <p className="text-sm text-muted-foreground">{t('childcare_statement.declaration_withheld')}</p>}
        </section>
      </PageHeader>
      {error && editing === null && !summarising ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
      {view.state === 'validated' ? <p>{t('childcare_statement.read_only')}</p> : null}
      {editable && view.cmg == null ? <p className="text-sm text-muted-foreground">{t('childcare_statement.validate_needs_cmg')}</p> : null}

      <Section title={t('childcare_statement.days')}
        actions={editable ? <Button type="button" variant="outline" onClick={() => { setError(''); setSummarising(true); }}>{t('childcare_statement.summary.open')}</Button> : null}>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t('childcare_statement.col_day')}</TableHead>
              <TableHead>{t('childcare_statement.col_presence')}</TableHead>
              <TableHead>{t('childcare_statement.col_hours')}</TableHead>
              <TableHead>{t('childcare_statement.col_meal')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {view.days.map((d, i) => (
              <DayRow key={d.date} day={d} problems={problemsOf(i).map((p) => problemText(t, p.code))}
                onEdit={editable ? () => { setError(''); setEditing(i); } : null} />
            ))}
          </TableBody>
        </Table>
      </Section>

      {editable ? (
        <Section title={t('childcare_statement.costs')}>
          <form noValidate className="flex flex-wrap items-end gap-3" onSubmit={(e) => { e.preventDefault(); send(days); }}>
            <Field label={t('childcare_statement.mileage')}>{(c) => <Input {...c} inputMode="decimal" className="w-36" value={mileage} onChange={(e) => setMileage(e.target.value)} />}</Field>
            <Field label={t('childcare_statement.cmg_field')} tip={t('childcare_detail.cmg_tip')}
              hint={cmg.trim() === '' && view.cmgEstimate ? t('childcare_statement.cmg_estimate', { amount: frenchNumber(view.cmgEstimate) }) : undefined}>
              {(c) => <Input {...c} inputMode="decimal" className="w-36" value={cmg} onChange={(e) => setCmg(e.target.value)} />}
            </Field>
            <Field label={t('childcare_statement.contributions_field')} hint={t('childcare_statement.contributions_hint')}>
              {(c) => <Input {...c} inputMode="decimal" className="w-36" value={contributions} onChange={(e) => setContributions(e.target.value)} />}
            </Field>
            <Field label={t('childcare_statement.exemption_field')} tip={t('childcare_statement.exemption_tip')}
              hint={exemption.trim() === '' && view.figures?.exemption ? t('childcare_statement.exemption_estimate', { amount: frenchNumber(view.figures.exemption.amount) }) : undefined}>
              {(c) => <Input {...c} inputMode="decimal" className="w-36" value={exemption} onChange={(e) => setExemption(e.target.value)} />}
            </Field>
            <Button type="submit" variant="outline" disabled={save.isPending}>{t('childcare_statement.save_costs')}</Button>
          </form>
        </Section>
      ) : null}

      <Declaration declaration={view.figures?.declaration ?? null} computed={view.computed} mealCharges={view.mealCharges} missing={view.missingReferenceMonths} entry={entry} />

      {summarising ? (
        <MonthSummaryDialog error={error} pending={save.isPending} onClose={() => { setSummarising(false); setError(''); }}
          onSave={(summary) => send(days, () => setSummarising(false), summary)} />
      ) : null}

      {editing !== null ? (
        <DayDialog day={view.days[editing]!} error={error} pending={save.isPending} onClose={() => { setEditing(null); setError(''); }}
          onSave={(day) => send(days.map((d, i) => (i === editing ? day : d)), () => setEditing(null))} />
      ) : null}
    </article>
  );
}

function DayRow({ day, problems, onEdit }: { day: StatementDay; problems: string[]; onEdit: (() => void) | null }) {
  const { t } = useTranslation();
  const title = dayTitle(day.date);
  const presence = day.present ? (day.absence ? `${t('childcare_statement.present')} · ${reasonLabel(t, day.absence)}` : t('childcare_statement.present')) : day.absence ? reasonLabel(t, day.absence) : day.planned ? t('childcare_statement.absent') : t('childcare_statement.unplanned');
  return (
    <TableRow data-changed={day.changed || undefined} className={day.planned || day.present ? undefined : 'text-muted-foreground'}>
      <TableCell>
        {onEdit ? <Button type="button" variant="link" className="h-auto p-0" aria-label={t('childcare_statement.edit_day', { date: title })} onClick={onEdit}>{title}</Button> : title}
        {day.weekType ? <span className="ml-2 text-sm text-muted-foreground">{day.weekType}</span> : null}
        {day.changed ? <Badge variant="outline" className="ml-2">{t('childcare_statement.changed')}</Badge> : null}
        {problems.map((p) => <span key={p} className="block text-sm text-destructive">{p}</span>)}
      </TableCell>
      <TableCell>{presence}</TableCell>
      <TableCell className="tabular-nums">{day.present ? `${day.arrival} – ${day.departure}` : ''}</TableCell>
      <TableCell>{day.present ? [day.lunch ? t('childcare_statement.lunch') : '', day.snack ? t('childcare_statement.snack') : ''].filter(Boolean).join(' · ') : ''}</TableCell>
    </TableRow>
  );
}
