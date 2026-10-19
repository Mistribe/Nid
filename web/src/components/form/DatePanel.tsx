import { fr } from 'react-day-picker/locale';
import { Calendar } from '@/components/ui/calendar';

/** Loaded lazily by DateField so react-day-picker stays out of the initial bundle. */
export default function DatePanel({ selected, max, onSelect }: { selected?: Date; max?: Date; onSelect: (d: Date) => void }) {
  return (
    <Calendar mode="single" locale={fr} weekStartsOn={1} selected={selected} defaultMonth={selected} disabled={max ? { after: max } : undefined}
      onSelect={(d) => { if (d) onSelect(d); }} />
  );
}
