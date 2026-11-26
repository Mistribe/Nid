import { Link } from '@tanstack/react-router';
import { Baby, KeyRound, Landmark, Receipt, RefreshCw, Wallet, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import type { DashboardFigure } from '@/api/dashboard';
import { itemRoute } from '@/components/ItemLink';
import { InfoTip } from '@/components/InfoTip';
import { formatMoney, Money } from '@/components/Money';
import { ModuleTile } from '@/components/ModuleTile';
import { placeOf, tileOf } from '@/components/nav';
import { cn } from '@/lib/utils';
import '@/locales/calendar';

export type CalendarAmounts = components['schemas']['CalendarAmountsView'];
export type CalendarCharge = components['schemas']['CalendarChargeView'];
export type CalendarDay = components['schemas']['CalendarDayView'];

const KINDS = ['subscription', 'loan_instalment', 'property_cost', 'rent', 'childcare', 'income'] as const;
type Kind = (typeof KINDS)[number];
const ICON: Record<Kind, LucideIcon> = { subscription: RefreshCw, loan_instalment: Landmark, property_cost: Receipt, rent: KeyRound, childcare: Baby, income: Wallet };
const kindOf = (c: CalendarCharge): Kind => KINDS.find((k) => k === c.kind) ?? 'subscription';

/** A server total in the household currency, « Inconnu » when a rate is missing (`short` in a cell, the reason in its title). */
export function FigureText({ figure, short = false }: { figure: DashboardFigure; short?: boolean }) {
  const { t } = useTranslation();
  if (figure.total) return <Money value={figure.total} tabular={short} />;
  const reason = t('dashboard.unknown_rate', { code: figure.missingCurrency ?? '' });
  return short ? <span title={reason}>{t('calendar_page.unknown')}</span> : <>{reason}</>;
}

/** Issue #241: a day's totals, out then in, the heavy days in the warning colour. */
export function DayTotals({ day }: { day: CalendarDay }) {
  const { t } = useTranslation();
  return (
    <span className="flex min-w-0 flex-col items-end text-xs leading-tight">
      {day.out ? (
        <span className={cn('tabular-nums', day.heavy && 'font-semibold text-status-warning-text')}>
          <span className="sr-only">{day.heavy ? `${t('calendar_page.heavy')}, ` : ''}{t('calendar_page.day_out')} </span>
          −<FigureText figure={day.out} short />
        </span>
      ) : null}
      {day.in ? (
        <span className="tabular-nums text-muted-foreground">
          <span className="sr-only">{t('calendar_page.day_in')} </span>+<FigureText figure={day.in} short />
        </span>
      ) : null}
    </span>
  );
}

/**
 * Issue #241: a debit (a credit: a rent) expected on an item, in its own currency, linked to the item; a cell's shows no amount (its day's
 * total does). #755: a projected one (a childcare month not validated yet) reads « estimé », how it is dated and priced in an InfoTip.
 */
export function ChargeLink({ c, compact }: { c: CalendarCharge; compact: boolean }) {
  const { t } = useTranslation();
  const kind = kindOf(c);
  const place = placeOf(c.module);
  const label = t(`calendar_page.charge.${kind}`);
  const estimated = c.estimated ? <span className="text-muted-foreground"> ({t('calendar_page.estimated')})</span> : null;
  const link = (
    <Link {...itemRoute(c.module, c.id)} title={compact ? `${label} · ${c.name}${c.estimated ? ` (${t('calendar_page.estimated')})` : ''}` : undefined}
      className={cn('flex min-w-0 items-center gap-2 rounded-sm px-1 hover:bg-accent', compact ? 'text-xs' : 'flex-1 py-1 text-sm')}>
      <ModuleTile icon={ICON[kind]} tile={place ? tileOf(place).tile : 'neutral'} />
      <span className="min-w-0 flex-1 truncate">
        {compact ? <>{c.name}{estimated}</> : <><span className="font-medium">{label}</span>{estimated}<span className="text-muted-foreground"> · {c.name}</span></>}
      </span>
      {compact ? null : <span className="shrink-0 tabular-nums">{c.credit ? '+' : '−'}{formatMoney(c.amount)}</span>}
    </Link>
  );
  // The InfoTip beside the link, not in it: a button inside a link is not reachable on its own.
  return c.estimated && !compact
    ? <div className="flex min-w-0 items-center gap-1">{link}<InfoTip label={`${label} (${t('calendar_page.estimated')})`}>{t('calendar_page.estimated_tip')}</InfoTip></div>
    : link;
}
