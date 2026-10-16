import { useSyncExternalStore } from 'react';

// One message for the proposal pages, kept across the navigation a decision triggers (spec §4).
let message = '';
const listeners = new Set<() => void>();

export function useAnnouncement() {
  const current = useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => message);
  return { message: current, announce: (text: string) => { message = text; listeners.forEach((l) => l()); } };
}

export function resetAnnouncement() { message = ''; }
