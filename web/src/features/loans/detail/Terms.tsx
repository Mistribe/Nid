/** Labelled figures in two columns: a term, then its value as the server wrote it. */
export function Terms({ items }: { items: [string, string][] }) {
  return (
    <dl className="grid gap-x-6 gap-y-1 sm:grid-cols-2">
      {items.map(([term, value]) => (
        <div key={term} className="flex flex-wrap justify-between gap-2 border-b py-1">
          <dt className="text-muted-foreground">{term}</dt>
          <dd className="tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}
