import { ChevronDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import type { IncomePhase } from '../api';
import '@/locales/income';

/** The id the page gives the focus back to when the change dialog closes unsaved. */
export const CHANGE_AMOUNT_ID = 'change-amount';

/**
 * Income dashboard §4, the header's main action: « Changer le montant » of the one component that can change, or a menu
 * naming each one when several can; nothing when none can.
 */
export function ChangeAmountAction({ phases, onChange }: { phases: IncomePhase[]; onChange: (id: string) => void }) {
  const { t } = useTranslation();
  if (phases.length === 0) return null;
  if (phases.length === 1)
    return <Button id={CHANGE_AMOUNT_ID} type="button" onClick={() => onChange(phases[0]!.id)}>{t('income_detail.change.open')}</Button>;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button id={CHANGE_AMOUNT_ID} type="button">{t('income_detail.change.open')}<ChevronDown aria-hidden="true" /></Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {phases.map((p) => <DropdownMenuItem key={p.id} onSelect={() => onChange(p.id)}>{p.label}</DropdownMenuItem>)}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
