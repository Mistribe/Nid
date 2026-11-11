import { RotateCcw } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { InfoTip } from '@/components/InfoTip';
import { Field } from '@/components/form/Field';
import { monthText } from '@/components/LocalDate';
import { Folded } from '@/components/form/Folded';
import { SectionCard } from '@/components/page/SectionCard';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { toFieldAmount } from '@/lib/amount';
import { ASSET_CLASSES, YEARLY_CLASSES, percent, projectionYears, wire, type Assumptions } from './api';
import { useAssumptionsPatch } from './useAssumptionsPatch';

type Table = Record<string, string>;

/** A server table as the fields show it (« 2,5 »). */
const fields = (table: Table) => Object.fromEntries(Object.entries(table).map(([k, v]) => [k, toFieldAmount(v)]));

/** The typed years only, as the wire reads them: an empty year is not sent, so an empty table clears the class's years. */
const typed = (byYear: Table) => Object.fromEntries(Object.entries(byYear).filter(([, v]) => v.trim()).map(([y, v]) => [y, wire(v)]));

/** « par défaut : 6 % » under a value that differs from its default; a click puts the default back in the field. */
function Reset({ value, fallback, name, onReset }: { value: string; fallback: string; name: string; onReset: (v: string) => void }) {
  const { t } = useTranslation();
  if (wire(value) === fallback) return null;
  return (
    <Button type="button" variant="link" size="sm" className="h-auto p-0 text-xs whitespace-normal text-muted-foreground"
      aria-label={t('settings.projection.guided.reset', { name, percent: percent(fallback) })} onClick={() => onReset(toFieldAmount(fallback))}>
      <RotateCcw aria-hidden="true" className="size-3" />{t('settings.projection.guided.default', { percent: percent(fallback) })}
    </Button>
  );
}

const Tipped = ({ label, tip }: { label: string; tip: string }) => (
  <span className="inline-flex items-center gap-1.5 text-sm text-muted-foreground">{label}<InfoTip label={label}>{tip}</InfoTip></span>
);

function SaveRow({ pending }: { pending: boolean }) {
  const { t } = useTranslation();
  return <div className="flex justify-end"><Button type="submit" variant="outline" disabled={pending}>{t('settings.projection.save')}</Button></div>;
}

/** Every class's rate and volatility, one « Enregistrer » for the table. */
function ClassTable({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { save, pending, errorOf, problem } = useAssumptionsPatch(['classRates', 'classVolatility']);
  const [rates, setRates] = useState(() => fields(a.classRates));
  const [volatility, setVolatility] = useState(() => fields(a.classVolatility));
  const cell = (values: Table, set: (f: (all: Table) => Table) => void, defaults: Table, c: string, label: string, path: string): ReactNode => (
    <div className="min-w-0 space-y-1">
      <Input aria-label={label} inputMode="decimal" autoComplete="off" value={values[c] ?? ''} aria-invalid={errorOf(path) ? true : undefined}
        onChange={(e) => set((all) => ({ ...all, [c]: e.target.value }))} />
      <Reset value={values[c] ?? ''} fallback={defaults[c] ?? '0'} name={label} onReset={(v) => set((all) => ({ ...all, [c]: v }))} />
      {errorOf(path) ? <p className="text-xs text-destructive">{errorOf(path)}</p> : null}
    </div>
  );

  return (
    <SectionCard level={3} title={t('settings.projection.guided.classes_title')} className="space-y-3">
      {problem}
      <form noValidate className="space-y-3" onSubmit={(event) => {
        event.preventDefault();
        save({
          classRates: Object.fromEntries(ASSET_CLASSES.map((c) => [c, wire(rates[c] ?? '')])),
          classVolatility: Object.fromEntries(ASSET_CLASSES.map((c) => [c, wire(volatility[c] ?? '')])),
        });
      }}>
        <div className="grid grid-cols-[minmax(0,1fr)_5rem_5rem] items-start gap-x-3 gap-y-3 sm:grid-cols-[minmax(0,1fr)_7rem_7rem]">
          <span className="text-sm text-muted-foreground">{t('settings.projection.class_column')}</span>
          <Tipped label={t('settings.projection.guided.rate_column')} tip={t('settings.projection.tip.rate')} />
          <Tipped label={t('settings.projection.volatility_column')} tip={t('settings.projection.tip.volatility')} />
          {ASSET_CLASSES.map((c) => {
            const name = t(`settings.projection.class.${c}`);
            return [
              <span key={`${c}-name`} className="pt-2 text-sm">{name}</span>,
              <div key={`${c}-rate`}>{cell(rates, setRates, a.defaults.classRates, c, t('settings.projection.guided.rate_of', { name }), `classRates.${c}`)}</div>,
              <div key={`${c}-volatility`}>{cell(volatility, setVolatility, a.defaults.classVolatility, c, t('settings.projection.volatility_label', { name }), `classVolatility.${c}`)}</div>,
            ];
          })}
        </div>
        <SaveRow pending={pending} />
      </form>
    </SectionCard>
  );
}

/** Inflation and the cash and money market rates for this year and the next four, then the long-term inflation. */
function YearByYear({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { save, pending, errorOf, problem } = useAssumptionsPatch(['inflation', 'inflationByYear', 'classRatesByYear']);
  const [years] = useState(projectionYears);
  const [inflation, setInflation] = useState(toFieldAmount(a.inflation));
  const [inflationYears, setInflationYears] = useState<Table>(() => Object.fromEntries(years.map((y) => [y, toFieldAmount(a.inflationByYear[y] ?? '')])));
  const [classYears, setClassYears] = useState<Record<string, Table>>(() =>
    Object.fromEntries(YEARLY_CLASSES.map((c) => [c, Object.fromEntries(years.map((y) => [y, toFieldAmount(a.classRatesByYear[c]?.[y] ?? '')]))])));
  const inflationName = t('settings.projection.inflation');
  const observed = a.observedInflation;
  const row = (name: string, values: Table, set: (y: string, v: string) => void, path: string, placeholder: string, tip: string, first?: string) => (
    <fieldset className="space-y-2">
      <legend className="flex items-center gap-1.5 text-sm font-medium">{name}<InfoTip label={name}>{tip}</InfoTip></legend>
      <div className="grid grid-cols-3 gap-3 sm:grid-cols-5">
        {years.map((y, i) => (
          <Field key={y} label={t('settings.projection.year_label', { name, year: y })} error={errorOf(`${path}.${y}`)} hint={i === 0 ? first : undefined}>
            {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" placeholder={i === 0 && first ? undefined : placeholder} value={values[y] ?? ''} onChange={(e) => set(y, e.target.value)} />}
          </Field>
        ))}
      </div>
    </fieldset>
  );

  return (
    <SectionCard level={3} title={t('settings.projection.guided.years_title')} className="space-y-3">
      {problem}
      <p className="text-sm text-muted-foreground">{t('settings.projection.guided.years_hint')}</p>
      <form noValidate className="space-y-4" onSubmit={(event) => {
        event.preventDefault();
        save({
          inflation: wire(inflation),
          inflationByYear: typed(inflationYears),
          classRatesByYear: Object.fromEntries(YEARLY_CLASSES.map((c) => [c, typed(classYears[c] ?? {})])),
        });
      }}>
        {row(inflationName, inflationYears, (y, v) => setInflationYears((all) => ({ ...all, [y]: v })), 'inflationByYear', inflation, t('settings.projection.tip.inflation'),
          observed && !inflationYears[years[0]!]?.trim() ? t('settings.projection.observed_hint', { percent: observed.percent.replace('.', ','), month: monthText(observed.to) }) : undefined)}
        <div className="w-40">
          <Field label={t('settings.projection.guided.long_term_inflation')} error={errorOf('inflation')}>
            {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={inflation} onChange={(e) => setInflation(e.target.value)} />}
          </Field>
          <Reset value={inflation} fallback={a.defaults.inflation} name={t('settings.projection.guided.long_term_inflation')} onReset={setInflation} />
        </div>
        {YEARLY_CLASSES.map((c) => {
          const name = t(`settings.projection.class.${c}`);
          return <div key={c}>{row(name, classYears[c] ?? {}, (y, v) => setClassYears((all) => ({ ...all, [c]: { ...all[c], [y]: v } })), `classRatesByYear.${c}`,
            toFieldAmount(a.classRates[c] ?? ''), t('settings.projection.tip.class_years'))}</div>;
        })}
        <SaveRow pending={pending} />
      </form>
    </SectionCard>
  );
}

/** The share of the capital drawn each year once retired, with its default. */
function Withdrawal({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { save, pending, errorOf, problem } = useAssumptionsPatch(['withdrawalRate']);
  const [rate, setRate] = useState(toFieldAmount(a.withdrawalRate));
  const label = t('settings.projection.withdrawal_rate');
  return (
    <SectionCard level={3} title={label} className="space-y-3">
      {problem}
      <form noValidate className="flex flex-wrap items-start gap-2" onSubmit={(event) => { event.preventDefault(); save({ withdrawalRate: wire(rate) }); }}>
        <div className="w-40 space-y-1">
          <Field label={t('settings.projection.guided.withdrawal_label')} tip={<InfoTip label={label}>{t('settings.projection.tip.withdrawal_rate')}</InfoTip>} error={errorOf('withdrawalRate')}>
            {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={rate} onChange={(e) => setRate(e.target.value)} />}
          </Field>
          <Reset value={rate} fallback={a.defaults.withdrawalRate} name={label} onReset={setRate} />
        </div>
        <Button type="submit" variant="outline" className="mt-6" disabled={pending}>{t('settings.projection.save')}</Button>
      </form>
    </SectionCard>
  );
}

/** « Personnaliser »: the only place with expert terms, each explained; closed by default. A saved change re-reads the fields. */
export function ProjectionCustomize({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  return (
    <Folded label={t('settings.projection.guided.customize')} hasError={false}>
      <ClassTable key={JSON.stringify([a.classRates, a.classVolatility])} a={a} />
      <YearByYear key={JSON.stringify([a.inflation, a.inflationByYear, a.classRatesByYear])} a={a} />
      <Withdrawal key={a.withdrawalRate} a={a} />
    </Folded>
  );
}
