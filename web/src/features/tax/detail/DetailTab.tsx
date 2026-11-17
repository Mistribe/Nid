import { ChevronDown } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { SectionCard } from '@/components/page/SectionCard';
import { TableToggle } from '@/components/page/TableToggle';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { todayIso } from '@/lib/today';
import { pistesOf } from '../overview/Pistes';
import { PerProgress, PerRows } from '../PerSection';
import { DeclarantTable, GROUP_TOTAL, RESULT_GROUPS, ResultTable, resultText } from '../ResultTable';
import type { TaxTab } from '../TaxPage';
import { BracketScale } from './BracketScale';
import { StepsChart, StepsTable } from './StepsChart';
import '@/locales/tax';

/** The calculation: its question and the steps from income to tax, the bracket, the salaries and the PER, then each group's table folded. */
export function DetailTab({ estimate, onTab }: { estimate: TaxEstimate; onTab: (tab: TaxTab) => void }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const r = estimate.result;
  const steps = r.steps ?? [];
  const hasPistes = pistesOf(t, estimate, Number(todayIso().slice(0, 4))).length > 0;
  return (
    <div className="animate-enter space-y-4">
      <div className="space-y-1">
        <h2 className="text-title">{t('tax.detail.question', { amount: resultText(t, r.totalTax) })}</h2>
        <Collapsible>
          <p className="text-sm text-muted-foreground">
            {t('tax.detail.disclaimer_short', { year: r.rulesYear })}{' '}
            <CollapsibleTrigger asChild><Button variant="link" className="h-auto p-0 text-sm">{t('tax.detail.more')}</Button></CollapsibleTrigger>
            {hasPistes ? <><span aria-hidden className="mx-2 text-muted-foreground">·</span><Button variant="link" className="h-auto p-0 text-sm" onClick={() => onTab('overview')}>{t('tax.detail.see_pistes')}</Button></> : null}
          </p>
          <CollapsibleContent><p className="pt-1 text-sm text-muted-foreground">{t('tax.disclaimer', { year: r.rulesYear })}</p></CollapsibleContent>
        </Collapsible>
      </div>
      {steps.length ? (
        <SectionCard title={t('tax.steps.title')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />} className="space-y-4">
          {asTable ? <StepsTable steps={steps} /> : (
            <>
              <StepsChart title={t('tax.steps.income')} steps={steps.filter((s) => s.group === 'income')} />
              <StepsChart title={t('tax.steps.tax')} steps={steps.filter((s) => s.group === 'tax')} legend />
            </>
          )}
        </SectionCard>
      ) : null}
      {r.bracket ? (
        <SectionCard title={t('tax.bracket.title')}><BracketScale bracket={r.bracket} marginalRate={r.marginalRate} /></SectionCard>
      ) : null}
      <SectionCard title={t('tax.detail.salaries_per')} className="space-y-4">
        <DeclarantTable result={r} detail={estimate.salaryDetail} adults={estimate.adults}>
          <PerRows result={r} names={r.declarants.map((_, i) => (estimate.inputs.declarants[i]?.perEnvelopes ?? []).join(', '))} />
        </DeclarantTable>
        <PerProgress result={r} onProfile={() => onTab('profile')} />
      </SectionCard>
      {RESULT_GROUPS.map((g) => <GroupCard key={g} group={g} estimate={estimate} />)}
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </div>
  );
}

function GroupCard({ group: g, estimate }: { group: (typeof RESULT_GROUPS)[number]; estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const result = estimate.result;
  return (
    <Collapsible>
      <SectionCard title={t(`tax.group.${g}`)} note={resultText(t, GROUP_TOTAL[g](result))} actions={
        <CollapsibleTrigger asChild>
          <Button variant="ghost" size="sm" className="group" aria-label={`${t('tax.detail.show_detail')} : ${t(`tax.group.${g}`)}`}>
            {t('tax.detail.show_detail')}<ChevronDown aria-hidden className="transition-transform group-data-[state=open]:rotate-180" />
          </Button>
        </CollapsibleTrigger>
      }>
        <CollapsibleContent>
          <ResultTable groups={[g]} declarants={false} label={t(`tax.group.${g}`)} columns={[{ title: t('tax.estimate'), result }]} />
        </CollapsibleContent>
      </SectionCard>
    </Collapsible>
  );
}
