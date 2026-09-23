// Amounts arrive from the server as decimal strings and are only ever displayed (frontend/README.md §2,
// principle 1): every total, cost and difference is computed server side. An amount is recognised by its
// name, which is how the generated client names them: `amount`, or anything ending in `Amount`.
const ARITHMETIC = new Set(['+', '-', '*', '/', '%', '**']);
const COERCIONS = new Set(['Number', 'parseFloat', 'parseInt', 'Number.parseFloat', 'Number.parseInt']);

/** @param {import('estree').Node | null | undefined} node */
function isAmount(node) {
  if (!node) return false;
  switch (node.type) {
    case 'Identifier':
      return /^amount$|Amount$/.test(node.name);
    case 'MemberExpression':
      return !node.computed && node.property.type === 'Identifier' && /^amount$|Amount$/.test(node.property.name);
    case 'ChainExpression':
      return isAmount(node.expression);
    default:
      // TypeScript wrappers: p.amount!, p.amount as string
      return 'expression' in node && (node.type === 'TSNonNullExpression' || node.type === 'TSAsExpression')
        ? isAmount(/** @type {any} */ (node).expression)
        : false;
  }
}

/** @param {import('estree').Expression | import('estree').Super} callee */
function calleeName(callee) {
  if (callee.type === 'Identifier') return callee.name;
  if (callee.type === 'MemberExpression' && !callee.computed && callee.object.type === 'Identifier' && callee.property.type === 'Identifier')
    return `${callee.object.name}.${callee.property.name}`;
  return null;
}

/** @type {import('eslint').Rule.RuleModule} */
export default {
  meta: {
    type: 'problem',
    docs: { description: 'Forbid arithmetic on, and numeric conversion of, amounts: the server computes them.' },
    schema: [],
    messages: {
      arithmetic: 'No arithmetic on amounts: the server computes every total, cost and difference.',
      coercion: 'Never turn an amount into a number: display the decimal string with <Money>.',
    },
  },
  create(context) {
    return {
      BinaryExpression(node) {
        if (ARITHMETIC.has(node.operator) && (isAmount(node.left) || isAmount(node.right)))
          context.report({ node, messageId: 'arithmetic' });
      },
      AssignmentExpression(node) {
        if (node.operator !== '=' && ARITHMETIC.has(node.operator.slice(0, -1)) && (isAmount(node.left) || isAmount(node.right)))
          context.report({ node, messageId: 'arithmetic' });
      },
      UnaryExpression(node) {
        if ((node.operator === '-' || node.operator === '+') && isAmount(node.argument))
          context.report({ node, messageId: 'arithmetic' });
      },
      UpdateExpression(node) {
        if (isAmount(node.argument)) context.report({ node, messageId: 'arithmetic' });
      },
      CallExpression(node) {
        const name = calleeName(node.callee);
        if (name && (COERCIONS.has(name) || name.startsWith('Math.')) && node.arguments.some((a) => isAmount(/** @type {any} */ (a))))
          context.report({ node, messageId: 'coercion' });
      },
    };
  },
};
