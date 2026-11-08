import { useTranslation } from 'react-i18next';
import { Money } from '@/components/Money';
import { Badge } from '@/components/ui/badge';
import { Progress } from '@/components/ui/progress';
import { cn } from '@/lib/utils';
import type { GoalSummary } from '@/api/goals';
import { pctText, statusText } from './format';
import '@/locales/goals';

// Status colours are the fixed ones (dataviz.md); the label always says it too.
const TONE: Record<string, string> = {
  reached: 'bg-status-good/15 text-status-good-text',
  on_track: 'bg-status-good/15 text-status-good-text',
  behind: 'bg-status-warning/15 text-status-warning-text',
  past_due: 'bg-status-critical/15 text-foreground',
  no_date: 'bg-muted text-muted-foreground',
};

/** A figure the server did not give: a dash on screen, `label` (« Non disponible » by default) for assistive technology. */
export function Dash({ label }: { label?: string }) {
  const { t } = useTranslation();
  return <span className="text-muted-foreground"><span aria-hidden="true">{t('goals_list.dash')}</span><span className="sr-only">{label ?? t('goals_list.no_value')}</span></span>;
}

/** The status as a badge; a goal without computed figures has none, and says a dash. */
export function GoalStatus({ status }: { status: string | null | undefined }) {
  const { t } = useTranslation();
  if (!status) return <Dash label={t('goals_list.no_status')} />;
  return <Badge variant="outline" className={cn('border-transparent', TONE[status])}>{statusText(t, status)}</Badge>;
}

/** The server's `progressPct` (capped at 100); never computed here. */
export function GoalProgress({ pct }: { pct: number | null | undefined }) {
  const { t } = useTranslation();
  if (pct === null || pct === undefined) return <Dash />;
  return (
    <span className="inline-flex items-center gap-2">
      <Progress value={pct} aria-label={t('goals_list.progress')} className="w-24" />
      <span className="tabular-nums">{t('goals_list.pct', { value: pctText(pct, 1) })}</span>
    </span>
  );
}

/** « atteint / cible »; figures the server could not compute are dashes, never 0 €. */
export function Amounts({ goal }: { goal: GoalSummary }) {
  const { t } = useTranslation();
  if (!goal.reached || !goal.target) return <Dash label={t('goals_list.no_figures')} />;
  return <span className="tabular-nums"><Money value={goal.reached} /> / <Money value={goal.target} /></span>;
}
