/** `?label=a,b` (1E2b2 design §3.3): a label key never holds a comma. Blanks and duplicates are dropped. */
export function labelsOf(value: string | undefined): string[] {
  return [...new Set((value ?? '').split(',').map((key) => key.trim()).filter((key) => key !== ''))];
}

export function labelParam(keys: string[]): string | undefined {
  return keys.length > 0 ? keys.join(',') : undefined;
}
