import { useEffect, useState } from 'react';

// A decision's message, handed to the page that mounts (or re-keys) after the navigation it triggers (spec §4).
let pending = '';

let refocus = false;

export const announce = (text: string) => { pending = text; };
export const resetAnnouncement = () => { pending = ''; refocus = false; };
/** A decision moved to another proposal: the next page heading takes focus (the form it came from is gone). */
export const focusNextHeading = () => { refocus = true; };
export const focusHeading = (el: HTMLElement | null) => { if (el && refocus) { refocus = false; el.focus(); } };

/** The status region's text: emptied at mount and whenever `key` changes, then filled once from the store, which it consumes. */
export function useAnnouncement(key?: string) {
  const [message, setMessage] = useState('');
  useEffect(() => {
    setMessage('');
    if (!pending) return;
    // Filled a moment after the emptying: the same message twice in a row is read again.
    const fill = setTimeout(() => { setMessage(pending); pending = ''; }, 100);
    return () => clearTimeout(fill);
  }, [key]);
  return message;
}
