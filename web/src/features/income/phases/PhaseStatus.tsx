import { CircleDot, Clock, History } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { statusText } from '../format';

const ICONS = { running: CircleDot, upcoming: Clock, past: History } as const;

/** Icon and word: never colour alone. A status the screen does not know reads as its raw name, without an icon. */
export function PhaseStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  const Icon = status in ICONS ? ICONS[status as keyof typeof ICONS] : null;
  return (
    <span className="inline-flex items-center gap-1">
      {Icon ? <Icon aria-hidden="true" className="size-4" /> : null}
      {statusText(t, status)}
    </span>
  );
}
