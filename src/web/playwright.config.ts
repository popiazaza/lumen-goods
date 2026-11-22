import { defineConfig, devices } from '@playwright/test';

const isCi = process.env['CI'] === 'true';

/**
 * Browser end-to-end tests against a running stack (`docker compose up --build -d --wait`), so they exercise nginx, the API, PostgreSQL, the background jobs and both partner stand-ins together.
 */
export default defineConfig({
  testDir: './e2e',
  // One customer is registered per run and the purchase specs share its server-side cart, so tests run one at a time. The API's per-IP limits on registration and sign-in rule out an account per worker.
  fullyParallel: false,
  workers: 1,
  forbidOnly: isCi,
  retries: isCi ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [[isCi ? 'github' : 'list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:8080',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts$/u },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['setup'],
    },
    {
      // 412 px wide, on Chromium, so CI installs one browser. Only tests tagged @phone run here.
      name: 'phone',
      use: { ...devices['Pixel 7'] },
      grep: /@phone/u,
      dependencies: ['setup'],
    },
  ],
});
