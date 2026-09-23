// Every string the user reads goes through i18next (frontend/architecture.md §11). Text with no letter
// (punctuation, separators) is allowed.
const LETTER = /\p{L}/u;
const TEXT_ATTRIBUTES = new Set(['aria-label', 'aria-description', 'title', 'placeholder', 'alt']);

// A JSXExpressionContainer that is a JSX child, or the value of a text attribute, can still hide a
// literal behind a ternary or `&&`: `{cond ? "Actif" : "Inactif"}`, `{cond && "Texte"}`,
// `` {`Bonjour ${name}`} ``. Only the branches of ConditionalExpression (never its `test`) and both
// sides of LogicalExpression are followed; anything else — a call argument like `t("…")`, an object,
// a comparison — stops the walk, so `t(cond ? "a.b" : "c.d")` and `className={cond ? "a" : "b"}` stay
// allowed.
function hasBypassedText(node) {
  if (!node) return false;
  switch (node.type) {
    case 'Literal':
      return typeof node.value === 'string' && LETTER.test(node.value);
    case 'TemplateLiteral':
      return node.quasis.some((quasi) => LETTER.test(quasi.value.raw));
    case 'ConditionalExpression':
      return hasBypassedText(node.consequent) || hasBypassedText(node.alternate);
    case 'LogicalExpression':
      return hasBypassedText(node.left) || hasBypassedText(node.right);
    default:
      return false;
  }
}

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
      JSXExpressionContainer(node) {
        const parent = /** @type {any} */ (node).parent;
        const isChild = parent.type === 'JSXElement';
        const isTextAttribute = parent.type === 'JSXAttribute'
          && parent.name.type === 'JSXIdentifier' && TEXT_ATTRIBUTES.has(parent.name.name);
        if ((isChild || isTextAttribute) && hasBypassedText(/** @type {any} */ (node).expression))
          context.report({ node, messageId: 'literal' });
      },
    };
  },
};
