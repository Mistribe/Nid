import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import '@/locales/admin';

// Status colours are the fixed ones (dataviz.md); the label always says it too.
const TONE: Record<string, string> = {
  succeeded: 'bg-status-good/15 text-status-good-text',
  failed: 'bg-status-critical/15 text-foreground',
  interrupted: 'bg-status-warning/15 text-status-warning-text',
  skipped: 'bg-muted text-muted-foreground',
  running: 'bg-muted text-muted-foreground',
};

/** How a run ended, as a badge with its word. */
export function JobOutcome({ outcome }: { outcome: string }) {
  const { t } = useTranslation();
  return <Badge variant="outline" className={cn('border-transparent', TONE[outcome])}>{t(`admin.jobs.outcomes.${outcome}`, { defaultValue: outcome })}</Badge>;
}
