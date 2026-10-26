import { canonicalize, hasLoneSurrogate } from '@/lib/canonical-json';
import { sha256Hex } from '@/lib/hash';
import { JsonNumber, JsonTextError, parseJson, serializeJson, type JsonValue } from '@/lib/json-text';

/** Bounds of import-format.md §11.2 and §11.5. */
export const BATCH_SIZE = 500;
export const MAX_BATCH_BYTES = 1_048_576;
export const MAX_DEPTH = 32;
/**
 * The server's default bounds (import-format.md §11.5, `ImportOptions.MaxStringLength` and
 * `MaxEntitiesPerSession`), configurable server-side: the server stays the authority if they are changed there.
 * A length is counted in UTF-16 code units, as C#'s `string.Length` and JS's `.length` both do.
 */
export const MAX_STRING_LENGTH = 10_000;
export const MAX_ENTITIES = 50_000;
export const FORMAT = 'nid.data';
export const VERSIONS: readonly number[] = [1];

/** The sections of import-format.md §3, in the commit order of §11.4: batches go out in this order. */
export const SECTIONS = [
  'persons', 'providers',
  'subscriptions', 'envelopes', 'properties', 'loans', 'income_sources',
  'billing_phases', 'purchases', 'valuations', 'flows', 'property_recurring_costs', 'tenancies', 'loan_phases',
  'loan_insurances', 'loan_fees', 'loan_events', 'loan_bank_schedule', 'income_phases', 'income_one_off_taxable',
  'holdings', 'holding_snapshots', 'holding_movements',
] as const;
const RESERVED = new Set(['documents']);
const ENVELOPE = new Set(['format', 'version', 'exported_at']);
const MAX_REPORTED = 100;

export type LocalProblemCode =
  | 'unreadable' | 'not_utf8' | 'not_json' | 'duplicate_member' | 'too_deep' | 'not_object' | 'format' | 'version'
  | 'unknown_section' | 'reserved_section' | 'not_array' | 'lone_surrogate' | 'nul' | 'number_out_of_range'
  | 'string_too_long' | 'too_many_entities' | 'batch_too_large' | 'empty';
export type LocalProblem = { code: LocalProblemCode; path?: string; line?: number; column?: number };
export type PreparedBatch = { section: string; index: number; count: number; hash: string; body: string };
export type FileAdult = { ref: string; displayName: string; email: string | null };
export type SectionCount = { name: string; count: number };
export type ReadyPreparation = {
  ok: true; format: string; version: number; sections: SectionCount[]; adults: FileAdult[]; batches: PreparedBatch[];
};
export type Prepared = ReadyPreparation | { ok: false; problems: LocalProblem[] };

const refuse = (...problems: LocalProblem[]): Prepared => ({ ok: false, problems });
const isSection = (name: string): boolean => (SECTIONS as readonly string[]).includes(name);

/**
 * The local check of web design §6.2 step 2, and the deterministic batches of step 4. Comfort only: the server
 * validates everything again. It refuses only what the server would refuse on receipt, so a file that passes here
 * is not left half-staged by a refusal the browser could have seen.
 */
export async function prepareFile(bytes: ArrayBuffer): Promise<Prepared> {
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); // a leading BOM is dropped
  } catch {
    return refuse({ code: 'not_utf8' });
  }

  let root: JsonValue;
  try {
    root = parseJson(text, { maxDepth: MAX_DEPTH });
  } catch (error) {
    if (!(error instanceof JsonTextError)) throw error;
    return refuse({ code: error.code === 'syntax' ? 'not_json' : error.code, line: error.line, column: error.column });
  }
  if (!(root instanceof Map)) return refuse({ code: 'not_object' });

  const problems: LocalProblem[] = [];
  if (root.get('format') !== FORMAT) problems.push({ code: 'format', path: 'format' });
  const version = root.get('version');
  const versionNumber = version instanceof JsonNumber ? Number(version.raw) : Number.NaN;
  if (!VERSIONS.includes(versionNumber)) problems.push({ code: 'version', path: 'version' });

  const sections: { name: string; entities: JsonValue[] }[] = [];
  for (const [name, value] of root) {
    if (ENVELOPE.has(name)) continue;
    if (RESERVED.has(name)) problems.push({ code: 'reserved_section', path: name });
    else if (!isSection(name)) problems.push({ code: 'unknown_section', path: name });
    else if (!Array.isArray(value)) problems.push({ code: 'not_array', path: name });
    else sections.push({ name, entities: value });
  }
  sections.sort((a, b) => SECTIONS.indexOf(a.name as (typeof SECTIONS)[number]) - SECTIONS.indexOf(b.name as (typeof SECTIONS)[number]));
  if (sections.reduce((total, s) => total + s.entities.length, 0) > MAX_ENTITIES) problems.push({ code: 'too_many_entities' });

  for (const section of sections) section.entities.forEach((entity, i) => scan(entity, `${section.name}[${i}]`, problems));
  if (problems.length > 0) return refuse(...problems.slice(0, MAX_REPORTED));
  if (sections.every((s) => s.entities.length === 0)) return refuse({ code: 'empty' });

  const encoder = new TextEncoder();
  const batches: PreparedBatch[] = [];
  for (const section of sections) {
    for (let index = 0; index * BATCH_SIZE < section.entities.length; index++) {
      const slice = section.entities.slice(index * BATCH_SIZE, (index + 1) * BATCH_SIZE);
      const hash = await sha256Hex(canonicalize(slice));
      const body = `{"hash":"${hash}","entities":${serializeJson(slice)}}`;
      if (encoder.encode(body).length > MAX_BATCH_BYTES) problems.push({ code: 'batch_too_large', path: `${section.name}[${index * BATCH_SIZE}]` });
      batches.push({ section: section.name, index, count: slice.length, hash, body });
    }
  }
  if (problems.length > 0) return refuse(...problems);

  return {
    ok: true,
    format: FORMAT,
    version: versionNumber,
    sections: sections.map((s) => ({ name: s.name, count: s.entities.length })),
    adults: adultsOf(sections.find((s) => s.name === 'persons')?.entities ?? []),
    batches,
  };
}

/** What the server refuses before staging a batch (import-protocol-design.md §10), with the server's path notation. */
function scan(value: JsonValue, path: string, problems: LocalProblem[]): void {
  if (problems.length >= MAX_REPORTED) return;
  if (typeof value === 'string') {
    if (hasLoneSurrogate(value)) problems.push({ code: 'lone_surrogate', path });
    else if (value.includes('\u0000')) problems.push({ code: 'nul', path });
    else if (value.length > MAX_STRING_LENGTH) problems.push({ code: 'string_too_long', path });
  } else if (value instanceof JsonNumber) {
    if (!Number.isFinite(Number(value.raw))) problems.push({ code: 'number_out_of_range', path });
  } else if (Array.isArray(value)) {
    value.forEach((item, i) => scan(item, `${path}[${i}]`, problems));
  } else if (value instanceof Map) {
    for (const [name, member] of value) {
      // A name is reported at its object: the server's path has no notation for a member name.
      if (hasLoneSurrogate(name)) problems.push({ code: 'lone_surrogate', path });
      else if (name.includes('\u0000')) problems.push({ code: 'nul', path });
      else if (name.length > MAX_STRING_LENGTH) problems.push({ code: 'string_too_long', path });
      scan(member, `${path}.${name}`, problems);
    }
  }
}

/** The adults of the file, for the mapping step (import-format.md §6). Anything else about them is the server's to check. */
function adultsOf(persons: JsonValue[]): FileAdult[] {
  return persons.flatMap((person) => {
    if (!(person instanceof Map) || person.get('kind') !== 'adult') return [];
    const ref = person.get('ref');
    if (typeof ref !== 'string') return [];
    const name = person.get('display_name');
    const email = person.get('email');
    return [{ ref, displayName: typeof name === 'string' ? name : ref, email: typeof email === 'string' ? email : null }];
  });
}
