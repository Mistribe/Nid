import { useTranslation } from 'react-i18next';
import { SectionCard } from '@/components/page/SectionCard';
import type { TaxResult } from '@/api/tax';
import { toPlot } from '@/lib/plot';
import { eur } from '../ResultTable';
import '@/locales/tax';

/** Where the net income goes: the bar is sized by plotting numbers (`toPlot`), every figure read is the server's string. */
export function IncomeSplit({ result: r }: { result: TaxResult }) {
  const { t } = useTranslation();
  const parts = [
    { label: t('tax.split.left'), amount: r.netAfterTax, colour: 'bg-muted-foreground/30' },
    { label: t('tax.row.income_tax'), amount: r.incomeTax, colour: 'bg-chart-1' },
    { label: t('tax.row.social_levies'), amount: r.socialLevies, colour: 'bg-chart-2' },
    { label: t('tax.row.flat_tax'), amount: r.capitalFlatTax, colour: 'bg-chart-3' },
  ].map((p) => ({ ...p, size: Math.max(0, toPlot(p.amount)), text: `${p.label} ${eur(p.amount)}` })).filter((p) => p.size > 0);
  if (parts.length < 2) return null;
  return (
    <SectionCard title={t('tax.split.title')} note={t('tax.split.note', { amount: eur(r.netBeforeTax) })} className="space-y-2">
      <div role="img" aria-label={parts.map((p) => p.text).join(', ')} className="flex h-3.5 gap-0.5 overflow-hidden rounded-full">
        {parts.map((p) => <div key={p.label} className={p.colour} style={{ flexGrow: p.size }} />)}
      </div>
      <ul aria-hidden="true" className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
        {parts.map((p) => <li key={p.label} className="flex items-center gap-1.5"><span className={`size-2.5 rounded-sm ${p.colour}`} />{p.text}</li>)}
      </ul>
    </SectionCard>
  );
}
