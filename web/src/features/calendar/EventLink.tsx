import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { DashboardEvent } from '@/api/dashboard';
import { ModuleTile } from '@/components/ModuleTile';
import { placeOf, tileOf } from '@/components/nav';
import { cn } from '@/lib/utils';
import { EventText, eventDetail, eventIcon, eventLink } from './events';

/** The tile of an event: its kind's icon on its module's colour. */
export function EventTile({ e }: { e: DashboardEvent }) {
  const place = placeOf(e.module);
  return <ModuleTile icon={eventIcon(e)} tile={place ? tileOf(place).tile : 'neutral'} />;
}

/** An event, with « À venir »'s icon and words, linked to its item (or to the tax page). */
export function EventLink({ e, compact }: { e: DashboardEvent; compact: boolean }) {
  const { t } = useTranslation();
  const detail = eventDetail(t, e);
  return (
    <Link {...eventLink(e)} title={compact ? detail : undefined}
      className={cn('flex min-w-0 items-center gap-2 rounded-sm px-1 hover:bg-accent', compact ? 'text-xs' : 'py-1 text-sm')}>
      <EventTile e={e} />
      <span className="min-w-0 flex-1 truncate">
        <span className="font-medium"><EventText e={e} /></span>
        <span className="text-muted-foreground"> · {detail}</span>
      </span>
    </Link>
  );
}
