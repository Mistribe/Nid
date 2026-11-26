import { CircleCheck } from 'lucide-react';
import type { LinkProps } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { tileOf, type Place } from '@/components/nav';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { SectionCard } from '@/components/page/SectionCard';
import type { TaxEstimate } from '@/api/tax';
import { todayIso } from '@/lib/today';
import { unsigned } from '../balance';
import { percentText, regimeText, ZERO } from '../format';
import { eur, wholeEur } from '../ResultTable';
import '@/locales/tax';

export type Piste = {
  key: 'per' | 'regime' | 'capital' | 'cut' | 'ceiling'; tile: Place; title: string; detail?: string; saving?: string; same?: boolean; link: LinkProps;
};

const tab = (name: string, hash?: string): LinkProps => ({ to: '/tax', search: (prev: object) => ({ ...prev, tab: name }), hash });

/** The actions the household can take, each with the server's figure (nothing computed here); in this order, each only when it holds. */
export function pistesOf(t: TFunction, { year, result: r }: TaxEstimate, thisYear: number): Piste[] {
  const out: Piste[] = [];
  const current = year === thisYear;
  // #726: a saving above the year's tax would turn it into a refund, said beside the tip (the server's figure).
  const refund = (amount?: string | null) => (amount ? t('tax.pistes.refund', { amount: wholeEur(amount) }) : undefined);
  const join = (...parts: (string | undefined)[]) => parts.filter(Boolean).join(' · ') || undefined;
  const minimums = r.declarants.filter((d) => d.per && !ZERO.test(d.per.minimum));
  // Worth a row only when it pays or a ceiling is about to be lost.
  if (current && r.per && !ZERO.test(r.per.remaining) && (!ZERO.test(r.per.remainingTaxSaved) || minimums.length)) {
    const detail = minimums.map((d) => t('tax.pistes.per.minimum', { name: d.name, amount: wholeEur(d.per!.minimum), expiring: wholeEur(d.per!.expiring) })).join(' · ');
    out.push({ key: 'per', tile: '/retirement', title: t('tax.pistes.per.title', { amount: wholeEur(r.per.remaining) }), detail: join(detail, refund(r.tipRefunds?.per)),
      saving: ZERO.test(r.per.remainingTaxSaved) ? undefined : r.per.remainingTaxSaved, link: tab('detail') });
  }
  const gap = r.rentalRegimeGap;
  if (gap) {
    const regime = regimeText(t, r.rentalRegime);
    const other = regimeText(t, r.rentalRegime === 'micro' ? 'real' : 'micro');
    const less = gap.startsWith('-') && !ZERO.test(gap);
    out.push({ key: 'regime', tile: '/properties', link: tab('profile', 'location'),
      title: less ? t('tax.pistes.regime.switch', { other }) : t('tax.pistes.regime.stay', { regime }),
      detail: less ? join(t('tax.pistes.regime.detail', { regime }), refund(r.tipRefunds?.rentalRegime)) : t('tax.pistes.regime.compared', { other }),
      ...(less ? { saving: unsigned(gap) } : { same: true }) });
  }
  const option = r.capitalOptionGap;
  if (option) {
    const less = option.startsWith('-') && !ZERO.test(option);
    const stay = less ? undefined : true;
    out.push({ key: 'capital', tile: '/investments', link: tab('profile', 'placements'),
      title: t(`tax.pistes.capital.${less ? (r.capitalScale ? 'switch_flat' : 'switch_scale') : (r.capitalScale ? 'stay_scale' : 'stay_flat')}`),
      detail: join(r.capitalScale && !ZERO.test(r.deductibleCsgNextYear) ? t('tax.capital_choice.csg', { amount: eur(r.deductibleCsgNextYear) }) : undefined,
        less ? refund(r.tipRefunds?.capitalOption) : undefined),
      ...(less ? { saving: unsigned(option) } : { same: stay }) });
  }
  const cut = r.withholding.cut;
  // #717: a cut asked too late to reach this year is no saving this year.
  if (current && cut && cut.months > 0) {
    out.push({ key: 'cut', tile: '/income', link: tab('withholding'), title: t('tax.pistes.cut.title', { amount: wholeEur(cut.less) }),
      detail: t('tax.pistes.cut.detail', { rate: percentText(t, cut.rate) }) });
  }
  if (r.warnings.includes('per_ceiling_estimated')) {
    out.push({ key: 'ceiling', tile: '/retirement', link: tab('profile', 'retraite'), title: t('tax.pistes.ceiling.title'),
      detail: t('tax.pistes.ceiling.detail', { names: r.declarants.filter((d) => d.per?.source === 'estimated').map((d) => d.name).join(', ') }) });
  }
  return out;
}

export function Pistes({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const pistes = pistesOf(t, estimate, Number(todayIso().slice(0, 4)));
  if (!pistes.length) return null;
  return (
    <SectionCard title={t('tax.pistes.title')}>
      <GroupedList>
        {pistes.map((p) => {
          // The saving is said once: under the title below `sm` (the trailing would crowd the title), trailing from `sm` up.
          const saving = p.saving ? (
            <span className="inline-flex items-center gap-1 text-status-good-text"><CircleCheck aria-hidden className="size-4" />{t('tax.pistes.saving', { amount: wholeEur(p.saving) })}</span>
          ) : p.same ? <span className="text-muted-foreground">{t('tax.pistes.nothing')}</span> : undefined;
          return (
            <GroupedRow key={p.key} tile={tileOf(p.tile)} link={p.link} title={p.title}
              detail={p.detail || saving ? <>{p.detail}{saving ? <span className="mt-0.5 block font-medium sm:hidden">{saving}</span> : null}</> : undefined}
              trailing={saving ? <span className="hidden sm:inline">{saving}</span> : undefined} />
          );
        })}
      </GroupedList>
    </SectionCard>
  );
}
