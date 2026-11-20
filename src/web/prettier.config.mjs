import config from 'ultracite/prettier';

// tailwindFunctions is an option of prettier-plugin-tailwindcss; this project has no Tailwind.
const { tailwindFunctions: _tailwindFunctions, ...ultracite } = config;

/**
@type {import('prettier').Config}
*/
export default {
  ...ultracite,
  // Kept from the project's previous .prettierrc (Angular CLI style).
  printWidth: 100,
  singleQuote: true,
  overrides: [{ files: '*.html', options: { parser: 'angular' } }],
};
