import { Link } from '@tanstack/react-router';
import { ChartLine } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import '@/locales/series';

/** « Voir la série »: opens the series explorer on these catalogue keys (`cpi`, `fx:EUR`…). */
export function SeriesLink({ keys }: { keys: readonly string[] }) {
  const { t } = useTranslation();
  return (
    <Button asChild variant="outline" size="sm">
      <Link to="/series" search={{ s: keys.join(',') }}><ChartLine aria-hidden="true" />{t('series.see')}</Link>
    </Button>
  );
}
