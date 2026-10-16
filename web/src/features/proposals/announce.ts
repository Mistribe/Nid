import { useEffect, useState } from 'react';

// A decision's message, handed to the page that mounts (or re-keys) after the navigation it triggers (spec §4).
let pending = '';

let refocus = false;

export const announce = (text: string) => { pending = text; };
export const resetAnnouncement = () => { pending = ''; refocus = false; };
/** A decision moved to another proposal: the next page heading takes focus (the form it came from is gone). */
export const focusNextHeading = () => { refocus = true; };
export const focusHeading = (el: HTMLElement | null) => { if (el && refocus) { refocus = false; el.focus(); } };

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
