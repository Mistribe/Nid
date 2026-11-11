import { Link } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SectionCard } from '@/components/page/SectionCard';
import { SeriesLink } from '@/components/page/SeriesLink';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Skeleton } from '@/components/ui/skeleton';
import { toFieldAmount, toWireAmount } from '@/lib/amount';
import { ProjectionCustomize } from './ProjectionCustomize';
import { SettingsFrame } from './SettingsFrame';
import { INFLATION, PROJECTION, PROJECTION_ASSUMPTIONS, PROJECTION_PROFILES, percent, type Assumptions } from './api';
import { useAssumptionsPatch } from './useAssumptionsPatch';

/** The places the savings go, as a household names them (#155); another stored class shows by its name. */
const SAVINGS_PLACES = ['cash', 'capital_guaranteed', 'equity', 'mixed'] as const;
const PROFILES = ['prudent', 'balanced', 'optimistic'] as const;
/** Income growth over prices, as the server writes it: the three plain choices. */
const GROWTH = { same: '0', more: '1', less: '-1' } as const;

/** « Combien mettez-vous de côté chaque mois ? »: automatic or an amount, where it goes, and the loans' freed payments. */
function SavingsCard({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { save, pending, errorOf, problem } = useAssumptionsPatch(['monthlySavings', 'savingsClass']);
  const [mode, setMode] = useState<'auto' | 'amount'>(a.monthlySavings ? 'amount' : 'auto');
  const [amount, setAmount] = useState(toFieldAmount(a.monthlySavings ?? ''));
  // The automatic figure is the base projection's, in the household scope.
  const projection = $api.useQuery('get', PROJECTION, { params: { query: { scope: 'household' } } }, { enabled: mode === 'auto' && !a.monthlySavings });
  const whereId = useId();
  const places: string[] = (SAVINGS_PLACES as readonly string[]).includes(a.savingsClass) ? [...SAVINGS_PLACES] : [...SAVINGS_PLACES, a.savingsClass];
  const chooseMode = (next: string) => {
    setMode(next as 'auto' | 'amount');
    if (next === 'auto' && a.monthlySavings) save({ monthlySavings: 'auto' });
  };
  const automatic = projection.data?.savingsOrigin === 'auto' ? projection.data.monthlySavings : null;

  return (
    <SectionCard title={t('settings.projection.guided.savings_title')} className="space-y-4">
      {problem}
      <div className="space-y-2">
        <Segmented aria-label={t('settings.projection.guided.savings_title')} value={mode} onValueChange={chooseMode} options={[
          { value: 'auto', label: t('settings.projection.guided.savings_auto') },
          { value: 'amount', label: t('settings.projection.guided.savings_amount') },
        ]} />
        {mode === 'auto' ? (
          <p className="text-sm text-muted-foreground">{automatic
            ? t('settings.projection.guided.savings_auto_figure', { amount: formatCurrency(automatic, a.currency, { maximumFractionDigits: 0 }) })
            : t('settings.projection.guided.savings_auto_hint')}</p>
        ) : (
          <form noValidate className="flex flex-wrap items-end gap-2" onSubmit={(event) => { event.preventDefault(); save({ monthlySavings: toWireAmount(amount) }); }}>
            <div className="w-48">
              <Field label={t('settings.projection.guided.per_month', { currency: a.currency })} error={errorOf('monthlySavings')}>
                {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={amount} onChange={(e) => setAmount(e.target.value)} />}
              </Field>
            </div>
            <Button type="submit" variant="outline" disabled={pending}>{t('settings.projection.save')}</Button>
          </form>
        )}
      </div>
      <div className="space-y-2">
        <p id={whereId} className="text-sm">{t('settings.projection.guided.savings_where')}</p>
        <Segmented aria-labelledby={whereId} value={a.savingsClass} onValueChange={(savingsClass) => save({ savingsClass })} disabled={pending}
          options={places.map((c) => ({
            value: c,
            label: (SAVINGS_PLACES as readonly string[]).includes(c) ? t(`settings.projection.guided.where.${c as (typeof SAVINGS_PLACES)[number]}`) : t(`settings.projection.class.${c as 'other'}`),
          }))} />
        {errorOf('savingsClass') ? <p className="text-sm text-destructive">{errorOf('savingsClass')}</p> : null}
      </div>
      <SwitchField label={t('settings.projection.guided.freed')} hint={t('settings.projection.guided.freed_hint')}
        checked={a.freedPaymentsToSavings} onCheckedChange={(freedPaymentsToSavings) => save({ freedPaymentsToSavings })} />
    </SectionCard>
  );
}

/** « Comment voyez-vous les marchés ? »: the three profiles, each with its effect in ten years, from the server. */
function MarketCard({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const profiles = $api.useQuery('get', PROJECTION_PROFILES);
  const { save, pending, problem } = useAssumptionsPatch([]);
  const legendId = useId();
  const effect = (key: string) => profiles.data?.profiles.find((p) => p.key === key);

  return (
    <SectionCard title={t('settings.projection.guided.market_title')} className="space-y-3">
      {problem}
      <p id={legendId} className="text-sm text-muted-foreground">{t('settings.projection.guided.market_hint')}</p>
      <RadioGroup aria-labelledby={legendId} value={a.profile === 'custom' ? '' : a.profile} disabled={pending} onValueChange={(profile) => save({ profile })}>
        {PROFILES.map((key) => {
          const p = effect(key);
          return (
            <label key={key} className="flex items-start gap-3 rounded-md border p-3 has-[[data-state=checked]]:border-primary">
              <RadioGroupItem value={key} className="mt-0.5" />
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-baseline justify-between gap-x-3">
                  <span className="font-medium">{t(`settings.projection.guided.profile.${key}`)}</span>
                  {p && profiles.data ? (
                    <span className="text-sm tabular-nums text-muted-foreground">
                      {t('settings.projection.guided.in_ten_years', { amount: formatCurrency(p.inTenYears, profiles.data.currency, { maximumFractionDigits: 0 }) })}
                    </span>
                  ) : null}
                </span>
                <span className="block text-sm text-muted-foreground">{t(`settings.projection.guided.profile_hint.${key}`)}</span>
              </span>
            </label>
          );
        })}
      </RadioGroup>
      {a.profile === 'custom' ? (
        <div className="rounded-md border border-dashed p-3">
          <p className="font-medium">{t('settings.projection.guided.custom')}</p>
          <p className="text-sm text-muted-foreground">{t('settings.projection.guided.custom_hint')}</p>
        </div>
      ) : null}
      {profiles.error ? <ProblemMessage problem={present(toProblem(profiles.error))} /> : null}
    </SectionCard>
  );
}

/** « De combien aurez-vous besoin par mois une fois à la retraite ? »: the living cost in today's money. */
function LivingCostCard({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { save, pending, errorOf, problem } = useAssumptionsPatch(['monthlyLivingCost']);
  const [amount, setAmount] = useState(toFieldAmount(a.monthlyLivingCost));
  return (
    <SectionCard title={t('settings.projection.guided.living_title')} className="space-y-3">
      {problem}
      <p className="text-sm text-muted-foreground">{t('settings.projection.guided.living_hint')}</p>
      <form noValidate className="flex flex-wrap items-end gap-2" onSubmit={(event) => { event.preventDefault(); save({ monthlyLivingCost: toWireAmount(amount) || '0' }); }}>
        <div className="w-48">
          <Field label={t('settings.projection.guided.per_month', { currency: a.currency })} error={errorOf('monthlyLivingCost')}>
            {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={amount} onChange={(e) => setAmount(e.target.value)} />}
          </Field>
        </div>
        <Button type="submit" variant="outline" disabled={pending}>{t('settings.projection.save')}</Button>
      </form>
    </SectionCard>
  );
}

/** « Vos revenus suivent-ils les prix ? »: the three plain choices; another stored value shows as « Personnalisé ». */
function IncomeGrowthCard({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { save, pending, problem } = useAssumptionsPatch([]);
  const known = (Object.values(GROWTH) as string[]).includes(a.incomeGrowth);
  const negative = a.incomeGrowth.startsWith('-');
  const custom = t(negative ? 'settings.projection.guided.income_custom_below' : 'settings.projection.guided.income_custom_above', { percent: percent(negative ? a.incomeGrowth.slice(1) : a.incomeGrowth) });
  return (
    <SectionCard title={t('settings.projection.guided.income_title')} className="space-y-2">
      {problem}
      <Segmented aria-label={t('settings.projection.guided.income_title')} value={a.incomeGrowth} disabled={pending} onValueChange={(incomeGrowth) => { if (incomeGrowth !== a.incomeGrowth) save({ incomeGrowth }); }}
        options={[
          { value: GROWTH.same, label: t('settings.projection.guided.income.same') },
          { value: GROWTH.more, label: t('settings.projection.guided.income.more') },
          { value: GROWTH.less, label: t('settings.projection.guided.income.less') },
          ...(known ? [] : [{ value: a.incomeGrowth, label: custom }]),
        ]} />
      <p className="text-sm text-muted-foreground">{t('settings.projection.guided.income_hint')}</p>
    </SectionCard>
  );
}

/** The prices: the projection's rate and the observed one, one line and one link to the series. */
function PricesCard({ a }: { a: Assumptions }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const observed = $api.useQuery('get', INFLATION);
  const last = observed.data?.last12Months;
  return (
    <SectionCard title={t('settings.projection.guided.prices_title')} actions={<SeriesLink keys={['projection:inflation', 'cpi:yoy']} />}>
      <p className="text-sm">
        {t(a.inflation === a.defaults.inflation ? 'settings.projection.guided.prices_line_ecb' : 'settings.projection.guided.prices_line', { percent: percent(a.inflation) })}
        {last ? ` ${t('settings.projection.guided.prices_observed', { percent: percent(last.percent) })}` : null}
      </p>
    </SectionCard>
  );
}

/** Réglages › Projection: plain questions, one card each, saving on its own; the expert values folded under « Personnaliser ». */
export function ProjectionSettingsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const assumptions = $api.useQuery('get', PROJECTION_ASSUMPTIONS);
  const a = assumptions.data;

  return (
    <SettingsFrame title={t('settings.projection.title')}>
      {a ? (
        <div className="space-y-4">
          <SavingsCard a={a} />
          <MarketCard a={a} />
          <LivingCostCard a={a} />
          <IncomeGrowthCard a={a} />
          <PricesCard a={a} />
          <SectionCard title={t('settings.projection.guided.retirement_title')}>
            <p className="text-sm">
              {t('settings.projection.guided.retirement_line')}{' '}
              <Link to="/settings/persons" className="underline underline-offset-4">{t('settings.projection.guided.retirement_link')}</Link>
            </p>
          </SectionCard>
          <ProjectionCustomize a={a} />
        </div>
      ) : assumptions.error ? <ProblemMessage problem={present(toProblem(assumptions.error))} />
        : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.projection.loading')}</span></Skeleton>}
    </SettingsFrame>
  );
}
