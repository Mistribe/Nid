import { fr } from 'react-day-picker/locale';
import { Calendar } from '@/components/ui/calendar';

/** Loaded lazily by DateField so react-day-picker stays out of the initial bundle. */
export default function DatePanel({ selected, min, max, onSelect }: { selected?: Date; min?: Date; max?: Date; onSelect: (d: Date) => void }) {
  return (
    <Calendar mode="single" locale={fr} weekStartsOn={1} selected={selected} defaultMonth={selected} disabled={[...(min ? [{ before: min }] : []), ...(max ? [{ after: max }] : [])]}
      onSelect={(d) => { if (d) onSelect(d); }} />
  );
}
