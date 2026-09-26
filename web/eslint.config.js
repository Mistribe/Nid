import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import nid from './eslint-rules/index.js';

export default defineConfig(
  { ignores: ['dist', 'src/api/schema.d.ts'] },
  js.configs.recommended,
  tseslint.configs.recommendedTypeChecked,
  {
    languageOptions: {
      globals: globals.browser,
      parserOptions: {
        projectService: {
          // e2e/ has its own tsconfig.json: its files would soon pass the default project's limit of 8.
          allowDefaultProject: ['*.js', 'eslint-rules/*.js', 'vite.config.ts', 'playwright.config.ts'],
          defaultProject: './tsconfig.node.json',
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      'no-restricted-syntax': ['error', {
        selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
        message: 'Imported labels, notes and model output are shown as plain text (frontend/architecture.md §9).',
      }],
    },
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: { nid },
    rules: { 'nid/no-amount-arithmetic': 'error', 'nid/no-literal-jsx-text': 'error' },
  },
  {
    files: ['src/**/*.test.{ts,tsx}', 'src/test/**'],
    rules: { 'nid/no-literal-jsx-text': 'off' },
  },
  {
    files: ['**/*.js', '**/*.mjs', 'public/**'],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: { globals: { ...globals.node, ...globals.browser } },
  },
);
