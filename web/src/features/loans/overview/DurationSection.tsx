import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import type { LoansSearch, RootSearch } from '@/app/router';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { Section } from '@/components/page/Section';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { personColor } from '@/features/dashboard/colors';
import { percent as sharePercent } from '@/features/dashboard/format';
import { toPlot } from '@/features/investments/chart/plot';
import { PERSONS } from '@/features/subscriptions/api';
import { todayIso } from '@/features/subscriptions/today';
import type { LoansOverview } from '../api';
import { modeText, percent } from '../format';
import { monthAfter, timeAxis, yearsLater } from './axis';
import { monthText, useLoanNames } from './BalanceChart';
import { loanColor } from './colors';
import '@/locales/loans';

type Span = LoansOverview['spans'][number];
type Phase = Span['phases'][number];
type Window = 'all' | '5y' | '10y';

const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');

/** Hatching for a deferral: the loan's colour in stripes, so a deferral never reads by colour alone. */
const hatched = (color: string) => `repeating-linear-gradient(135deg, ${color} 0 4px, transparent 4px 8px)`;

/**
 * Loans dashboard §4.2: each loan's span and phases, then the household's payment in steps on the same horizontal axis,
 * then each person's share. On a phone the timeline and the steps become lists.
 */
export function DurationSection({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const search: RootSearch & LoansSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const labelId = useId();
  const window: Window = search.span ?? 'all';
  const choose = (next: string) => void navigate({
    to: '/loans', replace: true,
    search: (prev: RootSearch & LoansSearch) => ({ ...prev, span: next === '5y' ? ('5y' as const) : next === '10y' ? ('10y' as const) : undefined }),
  });
  const today = todayIso();
  // The window bounds the axis: from today for 5 and 10 years, from the first disbursement to the last payment otherwise.
  const from = window === 'all' ? [today, ...d.spans.map((s) => s.start)].sort()[0]! : today;
  const to = window === 'all' ? [today, ...d.spans.map((s) => s.end)].sort().at(-1)! : yearsLater(today, window === '5y' ? 5 : 10);
  const axis = timeAxis(from, to);

  return (
    <>
      <SectionCard title={t('loans_overview.duration_title')} className="space-y-3" actions={
        <>
          <span id={labelId} className="sr-only">{t('loans_overview.span_label')}</span>
          <Segmented value={window} onValueChange={choose} aria-labelledby={labelId}
            options={(['5y', '10y', 'all'] as const).map((w) => ({ value: w, label: t(`loans_overview.span_${w}`) }))} />
        </>
      }>
        <div className="hidden md:block">
          <Timeline d={d} axis={axis} today={today} />
          <Steps d={d} axis={axis} today={today} />
        </div>
        <div className="space-y-4 md:hidden">
          <TimelineList d={d} today={today} />
          <StepsList d={d} />
        </div>
        <SpansTable d={d} />
      </SectionCard>
      <Persons d={d} />
    </>
  );
}

type Axis = ReturnType<typeof timeAxis>;

/** Left and width, in percent of the axis, of the interval from `start` to `end`. */
const place = (axis: Axis, start: string, end: string) => {
  const left = axis.at(start) * 100;
  return { left: `${left}%`, width: `${Math.max(0, axis.at(end) * 100 - left)}%` };
};

function phaseName(t: TFunction, phase: Phase): string {
  return phase.mode === 'annuity' ? t('loans_overview.phase_line', { mode: modeText(t, phase.mode), rate: percent(t, phase.annualRate) }) : modeText(t, phase.mode);
}

/** The phase running today, else the next one, else the last. */
const currentPhase = (span: Span, today: string) =>
  span.phases.find((p) => p.from <= today && today <= p.to) ?? span.phases.find((p) => p.from > today) ?? span.phases.at(-1);

function Timeline({ d, axis, today }: { d: LoansOverview; axis: Axis; today: string }) {
  const { t } = useTranslation();
  const { nameOf, rankOf } = useLoanNames(d);
  const rateOf = (id: string) => d.loans.find((l) => l.id === id)?.currentRate;
  return (
    <div aria-hidden="true" className="grid grid-cols-[11rem_1fr] gap-x-3 gap-y-2">
      {d.spans.map((span) => {
        const color = loanColor(rankOf(span.loanId));
        const rate = rateOf(span.loanId);
        return [
          <div key={`${span.loanId}/name`} className="min-w-0 self-center text-sm">
            <span className="block truncate font-medium">{nameOf(span.loanId)}</span>
            {rate ? <span className="text-xs text-muted-foreground">{percent(t, rate)}</span> : null}
          </div>,
          <Link key={`${span.loanId}/bar`} to="/loans/$id" params={{ id: span.loanId }} tabIndex={-1}
            className="relative block h-8 overflow-hidden rounded-sm bg-muted/40">
            {span.phases.map((phase) => (
              <span key={phase.seq} data-phase={phase.mode}
                style={{ ...place(axis, phase.from, monthAfter(phase.to.slice(0, 7))), background: phase.mode === 'annuity' ? color : hatched(color) }}
                className="absolute inset-y-0 border-r-2 border-background" />
            ))}
            {/* What is ahead of today reads faded; the names stay above it, readable. */}
            <span style={place(axis, today, span.end)} className="absolute inset-y-0 bg-background/55" />
            <span style={{ left: `${axis.at(today) * 100}%` }} className="absolute inset-y-0 w-0.5 bg-foreground" />
            {span.phases.map((phase) => (
              <span key={phase.seq} data-phase-label={phase.mode} style={place(axis, phase.from, monthAfter(phase.to.slice(0, 7)))}
                className="absolute inset-y-0 flex items-center overflow-hidden px-1.5">
                <span className="truncate rounded-sm bg-background/80 px-1 text-xs font-medium text-foreground">{phaseName(t, phase)}</span>
              </span>
            ))}
          </Link>,
        ];
      })}
      <div />
      <Ticks axis={axis} today={today} />
    </div>
  );
}

function Ticks({ axis, today }: { axis: Axis; today: string }) {
  const { t } = useTranslation();
  return (
    <div className="relative h-5 text-xs text-muted-foreground">
      {axis.ticks.map((tick) => (
        <span key={tick.year} style={{ left: `${tick.at * 100}%` }} className="absolute -translate-x-1/2 tabular-nums">{tick.year}</span>
      ))}
      <span style={{ left: `${axis.at(today) * 100}%` }} className="absolute top-5 -translate-x-1/2 font-medium text-foreground">{t('loans_overview.today')}</span>
    </div>
  );
}

const STEPS_HEIGHT = 112;

/** The household's payment in steps (D3), on the timeline's axis, each step labelled with its amount when it is wide enough. */
function Steps({ d, axis, today }: { d: LoansOverview; axis: Axis; today: string }) {
  const { t } = useTranslation();
  const max = Math.max(0, ...d.steps.map((s) => (s.amount ? toPlot(s.amount.amount) : 0)));
  const y = (v: number) => (max > 0 ? STEPS_HEIGHT - 16 - (v / max) * (STEPS_HEIGHT - 36) : STEPS_HEIGHT - 16);
  const steps = d.steps.map((s) => ({ ...s, x1: axis.at(`${s.from}-01`), x2: axis.at(monthAfter(s.to)), y: s.amount ? y(toPlot(s.amount.amount)) : null }));
  const path = steps.filter((s) => s.y !== null && s.x2 > s.x1)
    .map((s, i) => `${i === 0 ? 'M' : 'L'}${s.x1 * 1000},${s.y} H${s.x2 * 1000}`).join(' ');
  return (
    <div className="mt-6 grid grid-cols-[11rem_1fr] gap-x-3">
      <p className="self-start text-sm font-medium">{t('loans_overview.steps_title')}</p>
      <div aria-hidden="true" className="relative" style={{ height: STEPS_HEIGHT }}>
        <svg viewBox={`0 0 1000 ${STEPS_HEIGHT}`} preserveAspectRatio="none" className="absolute inset-0 size-full overflow-visible">
          <line x1={0} x2={1000} y1={STEPS_HEIGHT - 16} y2={STEPS_HEIGHT - 16} stroke="var(--border)" vectorEffect="non-scaling-stroke" />
          <path d={path} fill="none" stroke="var(--primary)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
        </svg>
        {steps.map((s) => (s.y !== null && s.x2 - s.x1 >= 0.08 ? (
          <span key={s.from} style={{ left: `${((s.x1 + s.x2) / 2) * 100}%`, top: s.y - 18 }}
            className="absolute -translate-x-1/2 whitespace-nowrap text-xs tabular-nums">{text(s.amount)}</span>
        ) : null))}
        <span style={{ left: `${axis.at(today) * 100}%` }} className="absolute inset-y-0 w-0.5 bg-foreground/40" />
      </div>
    </div>
  );
}

/** The phone's timeline: one loan a line, its start, end and the phase running. */
function TimelineList({ d, today }: { d: LoansOverview; today: string }) {
  const { t } = useTranslation();
  const { nameOf, rankOf } = useLoanNames(d);
  return (
    <ul className="divide-y rounded-lg border text-sm">
      {d.spans.map((span) => {
        const phase = currentPhase(span, today);
        return (
          <li key={span.loanId}>
            <Link to="/loans/$id" params={{ id: span.loanId }} className="flex items-start gap-2 p-3">
              <span aria-hidden="true" className="mt-1.5 size-2.5 shrink-0 rounded-full" style={{ background: loanColor(rankOf(span.loanId)) }} />
              <span className="space-y-0.5">
                <span className="block font-medium">{nameOf(span.loanId)}</span>
                <span className="block text-muted-foreground">{t('loans_overview.step_line', { from: formatLocalDate(span.start), to: formatLocalDate(span.end) })}</span>
                {phase ? <span className="block">{t('loans_overview.current_phase', { phase: phaseName(t, phase) })}</span> : null}
              </span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

function StepsList({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-1">
      <p className="text-sm font-medium">{t('loans_overview.steps_title')}</p>
      <ul className="divide-y rounded-lg border text-sm">
        {d.steps.map((s) => (
          <li key={s.from} className="flex justify-between gap-2 p-3">
            <span>{t('loans_overview.step_line', { from: monthText(s.from), to: monthText(s.to) })}</span>
            <span className="tabular-nums">{text(s.amount)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** What the drawing says, for assistive technology: each loan with its phases, then the steps. */
function SpansTable({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const { nameOf } = useLoanNames(d);
  return (
    <div className="sr-only">
      <Table aria-label={t('loans_overview.timeline_table')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('loans_overview.col_loan')}</TableHead>
            <TableHead scope="col">{t('loans_overview.col_start')}</TableHead>
            <TableHead scope="col">{t('loans_overview.col_end')}</TableHead>
            <TableHead scope="col">{t('loans_overview.col_phases')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {d.spans.map((s) => (
            <TableRow key={s.loanId}>
              <TableHead scope="row">{nameOf(s.loanId)}</TableHead>
              <TableCell>{formatLocalDate(s.start)}</TableCell>
              <TableCell>{formatLocalDate(s.end)}</TableCell>
              <TableCell>
                {s.phases.map((p) => t('loans_overview.phase_dates', { phase: phaseName(t, p), from: formatLocalDate(p.from), to: formatLocalDate(p.to) })).join(' ; ')}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <Table aria-label={t('loans_overview.steps_table')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('loans_overview.step_from')}</TableHead>
            <TableHead scope="col">{t('loans_overview.step_to')}</TableHead>
            <TableHead scope="col">{t('loans_overview.step_amount')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {d.steps.map((s) => (
            <TableRow key={s.from}>
              <TableCell>{monthText(s.from)}</TableCell>
              <TableCell>{monthText(s.to)}</TableCell>
              <TableCell>{text(s.amount)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

/** D5: one card a person, in the dashboard's colour rank (adults by creation, then dependents), as the owner chips. */
function Persons({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { nameOf } = useLoanNames(d);
  const people = $api.useQuery('get', PERSONS);
  if (d.byPerson.length === 0) return null;
  const ranked = [...(people.data ?? [])].sort((a, b) => Number(a.kind !== 'adult') - Number(b.kind !== 'adult'));
  return (
    <Section title={t('loans_overview.persons_title')}>
      <div className="grid gap-4 md:grid-cols-2">
        {d.byPerson.map((share, index) => {
          const slot = ranked.findIndex((p) => p.id === share.personId);
          const name = ranked[slot]?.displayName ?? t('loans_overview.unknown_person', { n: index + 1 });
          return (
            <SectionCard key={share.personId} title={name}
              note={<span aria-hidden="true" className="inline-block size-2.5 rounded-full" style={{ background: personColor(slot) }} />}>
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <div><dt className="text-muted-foreground">{t('loans_overview.person_balance')}</dt><dd className="font-semibold tabular-nums">{text(share.balance)}</dd></div>
                <div><dt className="text-muted-foreground">{t('loans_overview.person_monthly')}</dt><dd className="font-semibold tabular-nums">{text(share.monthlyPayment)}</dd></div>
              </dl>
              <ul className="mt-3 space-y-1 text-sm">
                {share.loans.map((l) => (
                  <li key={l.loanId} className="flex justify-between gap-2">
                    <span>{nameOf(l.loanId)} · {t('owner.pct', { value: sharePercent(l.shareBp / 100) })}</span>
                    <span className="tabular-nums">{text(l.balance)}</span>
                  </li>
                ))}
              </ul>
            </SectionCard>
          );
        })}
      </div>
    </Section>
  );
}

