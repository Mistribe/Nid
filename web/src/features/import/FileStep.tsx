import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/utils';
import type { ReadyFile } from './draft';
import type { LocalProblem } from './prepare';
import { prepareInBackground } from './prepareInBackground';

const SHOWN = 20;

type Reading = { status: 'idle' } | { status: 'reading' } | { status: 'failed'; problems: LocalProblem[] };

/** Step 1 and 2 of web design §6.2: the file, dropped or picked, and what the local check finds in it. */
export function FileStep({ title, hint, onReady }: { title: string; hint: string; onReady: (file: ReadyFile) => void }) {
  const { t } = useTranslation();
  const [reading, setReading] = useState<Reading>({ status: 'idle' });
  const [over, setOver] = useState(false);

  const read = async (file: File) => {
    setReading({ status: 'reading' });
    try {
      const prepared = await prepareInBackground(await file.arrayBuffer());
      if (prepared.ok) {
        setReading({ status: 'idle' });
        onReady({ ...prepared, name: file.name });
      } else setReading({ status: 'failed', problems: prepared.problems });
    } catch {
      setReading({ status: 'failed', problems: [{ code: 'unreadable' }] });
    }
  };

  return (
    // No `aria-labelledby` here: its text is the picker's own label text (both are "Choisir un fichier" in
    // fr.json), and `getByLabelText` also matches non-form elements labelled that way, which made the section
    // itself a second match for the label query alongside the input.
    <section className="space-y-3">
      <h2 id="import-file-title" className="text-lg font-semibold">{title}</h2>
      <div
        data-drop-zone=""
        onDragOver={(event) => { event.preventDefault(); setOver(true); }}
        onDragLeave={() => setOver(false)}
        onDrop={(event) => {
          event.preventDefault();
          setOver(false);
          const file = event.dataTransfer.files[0];
          if (file) void read(file);
        }}
        className={cn('space-y-3 rounded-md border-2 border-dashed p-6 text-center', over && 'border-primary')}
      >
        <p className="text-muted-foreground">{hint}</p>
        <label className="inline-block cursor-pointer rounded-md border px-3 py-2 focus-within:ring-2">
          {t('import.file_pick')}
          <input
            type="file"
            accept=".json,application/json"
            className="sr-only"
            onChange={(event) => {
              const file = event.currentTarget.files?.[0];
              event.currentTarget.value = ''; // choosing the same file again still triggers a read
              if (file) void read(file);
            }}
          />
        </label>
      </div>
      {reading.status === 'reading' ? (
        <div aria-busy="true" className="h-16 animate-pulse rounded-md bg-muted">
          <span className="sr-only">{t('import.reading')}</span>
        </div>
      ) : null}
      {reading.status === 'failed' ? <LocalProblems problems={reading.problems} /> : null}
    </section>
  );
}

function LocalProblems({ problems }: { problems: LocalProblem[] }) {
  const { t } = useTranslation();
  return (
    <div role="alert" className="space-y-1 rounded-md border border-destructive p-3">
      <ul className="list-disc pl-5">
        {problems.slice(0, SHOWN).map((problem, i) => (
          <li key={`${problem.code}-${problem.path ?? ''}-${i}`}>
            {t(`import.local.${problem.code}`, { path: problem.path, line: problem.line, column: problem.column })}
          </li>
        ))}
      </ul>
      {problems.length > SHOWN ? <p>{t('import.problems_more', { n: problems.length - SHOWN })}</p> : null}
    </div>
  );
}
