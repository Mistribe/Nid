import { ArrowDownRight, ArrowRight, ArrowUpRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatCurrency } from '@/components/Money';
import { directionOf } from '@/lib/amount';

const ICON = { up: ArrowUpRight, down: ArrowDownRight, flat: ArrowRight } as const;
const TONE = { up: 'text-status-good', down: 'text-status-critical', flat: 'text-muted-foreground' } as const;
const signed = (locale: string, currency: string, amount: string) => formatCurrency(amount, currency, { signDisplay: 'exceptZero' }, locale);

/** An amount with its direction as icon and word (the word for a screen reader), colour only on the icon (never alone, never on text); `children` sit between the amount and the word. */
export function Direction({ amount, children }: { amount: { amount: string; currency: string }; children?: ReactNode }) {
  const { t } = useTranslation();
  const direction = directionOf(amount.amount);
  const Icon = ICON[direction];
  return (
    <span className="inline-flex flex-wrap items-center gap-x-1">
      <Icon aria-hidden="true" className={`size-4 ${TONE[direction]}`} />
      <span className="tabular-nums">{signed('fr-FR', amount.currency, amount.amount)}</span>
      {children}
      {/* The sign and the arrow say it on screen; the word is for a screen reader. */}
      <span className="sr-only">{t(`investments.direction.${direction}`)}</span>
    </span>
  );
}
