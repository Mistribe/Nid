/**
 * A navigation entry's count (« Routine », « À valider »): a pill after the label, or a dot on the icon's corner when
 * the sidebar is a rail. The words stay for screen readers either way.
 */
export function CountBadge({ count, label, dot = false }: { count: number; label: string; dot?: boolean }) {
  if (dot) {
    return (
      <span className="absolute top-1.5 right-1.5 size-2 rounded-full bg-primary ring-2 ring-sidebar">
        <span className="sr-only">{label}</span>
      </span>
    );
  }
  return (
    <span className="rounded-full bg-primary px-1.5 text-xs font-medium tabular-nums text-primary-foreground">
      <span aria-hidden="true">{count}</span>
      <span className="sr-only">{label}</span>
    </span>
  );
}
