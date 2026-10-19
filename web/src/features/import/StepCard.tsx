import type { ReactNode } from 'react';
import { Card, CardContent } from '@/components/ui/card';

/** One step of the import as a card. */
export function StepCard({ children }: { children: ReactNode }) {
  return <Card><CardContent>{children}</CardContent></Card>;
}
