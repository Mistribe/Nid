/**
 * The i18next context for a French word after « de » : `vowel` when it elides (« Routine d’octobre », « mois d’avril »),
 * so the locale holds both forms (`title` / `title_vowel`). Made for month names: a mute « h » is not handled.
 */
export const elision = (word: string) => (/^[aeiouâàéèêîïôû]/i.test(word) ? 'vowel' : undefined);
