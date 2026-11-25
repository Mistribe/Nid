import { useEffect, useState } from 'react';
import { Input } from '@/components/ui/input';

/** #169: how long the typing pauses before the search goes: the server may ask a worker for its meaning, not on every key. */
const SEARCH_PAUSE_MS = 300;

/** The search field: what is typed shows at once, and goes to the URL (so to the server) after a pause in the typing. */
export function SearchInput({ id, labelledBy, value, onSearch }: { id: string; labelledBy: string; value: string; onSearch: (q: string) => void }) {
  const [text, setText] = useState(value);
  // The URL changed from elsewhere (a link, a filter dropped): the field follows it.
  const [sent, setSent] = useState(value);
  if (value !== sent) { setSent(value); setText(value); }
  useEffect(() => {
    if (text === value) return undefined;
    const timer = window.setTimeout(() => onSearch(text), SEARCH_PAUSE_MS);
    return () => window.clearTimeout(timer);
  }, [text, value, onSearch]);
  return <Input id={id} aria-labelledby={labelledBy} type="search" className="min-w-0 flex-1 sm:w-56 sm:flex-none" value={text} onChange={(e) => setText(e.target.value)} />;
}
