import { ChartLine, Table2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';

/**
 * « Afficher en tableau » / « Afficher le graphique » (dataviz.md §6): on a phone an icon alone, so a chart's header keeps
 * its controls on one line; the words stay its accessible name, and show from `sm` up.
 */
export function TableToggle({ asTable, onToggle }: { asTable: boolean; onToggle: () => void }) {
  const { t } = useTranslation();
  const Icon = asTable ? ChartLine : Table2;
  return (
    <Button type="button" variant="outline" size="sm" onClick={onToggle} className="max-sm:size-8 max-sm:px-0">
      <Icon aria-hidden="true" className="sm:hidden" />
      <span className="max-sm:sr-only">{asTable ? t('common.show_chart') : t('common.show_table')}</span>
    </Button>
  );
}
