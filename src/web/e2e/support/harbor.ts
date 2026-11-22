import { execFileSync } from 'node:child_process';

const PRICE_COLUMN = 2;

/**
 * Runs the Harbor stand-in's demo CLI inside the compose stack, from the repository root. It edits Harbor's own data the way the partner could at any time (stand-in README, "Demo CLI").
 */
const harborCli = (repoRoot: string, args: readonly string[]): string => {
  const command = [
    'compose',
    'exec',
    '-T',
    'harbor',
    'dotnet',
    'HarborSupply.StandIn.dll',
    ...args,
  ];
  // eslint-disable-next-line sonarjs/no-os-command-from-path -- the docker CLI that started the stack, here or on the CI runner
  return execFileSync('docker', command, { cwd: repoRoot, encoding: 'utf-8' });
};

/**
 * An item's price in whole cents, from `list` (columns: item code, department, price, ...).
 */
export const harborPriceCents = (repoRoot: string, itemCode: string): number => {
  const row = harborCli(repoRoot, ['list'])
    .split('\n')
    .find((line) => line.startsWith(`${itemCode} `));
  const cents = Number(row?.trim().split(/\s+/u, PRICE_COLUMN + 1)[PRICE_COLUMN]);
  if (!Number.isSafeInteger(cents)) {
    throw new TypeError(`Harbor's list has no price for ${itemCode}`);
  }
  return cents;
};

export const setHarborPriceCents = (repoRoot: string, itemCode: string, cents: number): void => {
  harborCli(repoRoot, ['set-price', itemCode, String(cents)]);
};
