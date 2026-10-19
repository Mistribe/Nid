import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import type { DashboardView } from '../api';
import type { Panel } from '../KeyFigures';
import { CommitmentsPanel } from './CommitmentsPanel';
import { IncomePanel } from './IncomePanel';
import { MarginPanel } from './MarginPanel';
import { NetWorthPanel } from './NetWorthPanel';

/** Spec §4.2: a side sheet (full screen on a phone) opened by `?panel=`; Radix returns focus to the figure's button. */
export function FigurePanel({ panel, onClose, d, scope = 'household' }: { panel: Panel | undefined; onClose: () => void; d: DashboardView; scope?: 'me' | 'household' }) {
  const { t } = useTranslation();
  // The sheet has no Trigger of its own: Radix would not know where to give focus back.
  const opener = useRef(document.activeElement);
  return (
    <Sheet open={panel !== undefined} onOpenChange={(open) => { if (!open) onClose(); }}>
      <SheetContent className="w-full overflow-y-auto sm:max-w-xl" aria-describedby={undefined}
        onCloseAutoFocus={(e) => { e.preventDefault(); if (opener.current instanceof HTMLElement) opener.current.focus(); }}>
        <SheetHeader>
          <SheetTitle>{panel ? t(`dashboard.key.${panel}`) : null}</SheetTitle>
        </SheetHeader>
        <div className="px-4 pb-4">
          {panel === 'net_worth' ? <NetWorthPanel d={d} /> : null}
          {panel === 'income' ? <IncomePanel d={d} scope={scope} /> : null}
          {panel === 'commitments' ? <CommitmentsPanel d={d} /> : null}
          {panel === 'margin' ? <MarginPanel d={d} /> : null}
        </div>
      </SheetContent>
    </Sheet>
  );
}
