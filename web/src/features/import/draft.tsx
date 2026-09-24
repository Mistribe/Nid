import { createContext, useContext, useState, type ReactNode } from 'react';
import type { ReadyPreparation } from './prepare';

export type ReadyFile = ReadyPreparation & { name: string };
type Draft = { file: ReadyFile | null; setFile: (file: ReadyFile | null) => void };

const DraftContext = createContext<Draft | null>(null);

/**
 * The prepared file lives above `/import` and `/import/$id`, so it survives starting a session. Leaving the import
 * area or closing the tab drops it: resuming means selecting the file again (web design §6.2).
 */
export function DraftProvider({ children }: { children: ReactNode }) {
  const [file, setFile] = useState<ReadyFile | null>(null);
  return <DraftContext.Provider value={{ file, setFile }}>{children}</DraftContext.Provider>;
}

export function useDraft(): Draft {
  const draft = useContext(DraftContext);
  if (!draft) throw new Error('useDraft() used outside <DraftProvider>.');
  return draft;
}
