// Every string the user reads goes through i18next (frontend/architecture.md §11). Text with no letter
// (punctuation, separators) is allowed.
const LETTER = /\p{L}/u;
const TEXT_ATTRIBUTES = new Set(['aria-label', 'aria-description', 'title', 'placeholder', 'alt']);

/** @type {import('eslint').Rule.RuleModule} */
export default {
  meta: {
    type: 'problem',
    docs: { description: 'Forbid literal user-visible text in JSX: use t("…").' },
    schema: [],
    messages: { literal: "Text shown to the user goes through i18n: t('…')." },
  },
  create(context) {
    return {
      JSXText(node) {
        if (LETTER.test(node.value)) context.report({ node, messageId: 'literal' });
      },
      JSXAttribute(node) {
        const value = node.value;
        if (node.name.type === 'JSXIdentifier' && TEXT_ATTRIBUTES.has(node.name.name)
            && value?.type === 'Literal' && typeof value.value === 'string' && LETTER.test(value.value))
          context.report({ node, messageId: 'literal' });
      },
      'JSXElement > JSXExpressionContainer > Literal'(node) {
        if (typeof node.value === 'string' && LETTER.test(node.value)) context.report({ node, messageId: 'literal' });
      },
    };
  },
};
