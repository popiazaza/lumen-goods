import config from 'ultracite/stylelint';

export default {
  ...config,
  // Playwright's HTML report and test results carry the trace viewer's built CSS.
  ignoreFiles: [...config.ignoreFiles, 'playwright-report/**', 'test-results/**'],
};
