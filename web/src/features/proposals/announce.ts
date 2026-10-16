import { useEffect, useState } from 'react';

// A decision's message, handed to the page that mounts (or re-keys) after the navigation it triggers (spec §4).
let pending = '';

export const announce = (text: string) => { pending = text; };
export const resetAnnouncement = () => { pending = ''; };

/** The status region's text: empty at mount, filled once from the store after mount or when `key` changes, then consumed. */
export function useAnnouncement(key?: string) {
  const [message, setMessage] = useState('');
  useEffect(() => {
    if (!pending) return;
    setMessage(pending);
    pending = '';
  }, [key]);
  return message;
}
