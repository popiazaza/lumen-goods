import { DOCUMENT } from '@angular/common';
import { Service, computed, inject, signal } from '@angular/core';
import { ShippingAddress, ShippingMethod } from '../api/order.models';

export const CHECKOUT_STEPS = ['address', 'shipping', 'review', 'payment'] as const;
export type CheckoutStep = (typeof CHECKOUT_STEPS)[number];

/** What the shopper has entered so far in this checkout. */
export interface CheckoutState {
  /** Sent with every Place order of this checkout, so a repeat returns the same order (BR-PAY-01). */
  checkoutKey: string;
  address: ShippingAddress | null;
  shippingMethod: ShippingMethod | null;
  /** Set once Place order succeeded: the order the payment step pays. */
  orderNumber: string | null;
}

/** sessionStorage key: per tab, gone when the tab closes, never shared with other tabs. */
export const CHECKOUT_STORAGE_KEY = 'lumen.checkout.v1';

/**
 * The checkout's state across its four routes. A root signal store mirrored to sessionStorage,
 * so Back, the stepper and a page refresh all keep what the shopper entered (WCAG 3.3.7).
 * Holds no prices: the review and payment steps always read them from the API.
 */
@Service()
export class CheckoutStore {
  private readonly storage = storageOf(inject(DOCUMENT));
  private readonly state = signal<CheckoutState>(this.restore());

  readonly checkoutKey = computed(() => this.state().checkoutKey);
  readonly address = computed(() => this.state().address);
  readonly shippingMethod = computed(() => this.state().shippingMethod);
  readonly orderNumber = computed(() => this.state().orderNumber);

  /** Steps the shopper may open now: done ones, plus the next one to do. */
  readonly reachable = computed<ReadonlySet<CheckoutStep>>(() => {
    const { address, shippingMethod, orderNumber } = this.state();
    const steps = new Set<CheckoutStep>(['address']);
    if (address) {
      steps.add('shipping');
    }
    if (address && shippingMethod) {
      steps.add('review');
    }
    if (orderNumber) {
      steps.add('payment');
    }
    return steps;
  });

  /**
   * A changed address after Place order starts a new checkout key: the placed order keeps its old
   * address, so the next Place order must create a new one (the unpaid one expires on its own).
   */
  setAddress(address: ShippingAddress): void {
    this.update((state) =>
      sameAddress(state.address, address) ? state : { ...afterChange(state), address },
    );
  }

  setShippingMethod(shippingMethod: ShippingMethod): void {
    this.update((state) =>
      state.shippingMethod === shippingMethod ? state : { ...afterChange(state), shippingMethod },
    );
  }

  setOrderNumber(orderNumber: string): void {
    this.update((state) => ({ ...state, orderNumber }));
  }

  /** Forgets this checkout (after payment, or on sign-out) and starts the next with a new key. */
  reset(): void {
    this.update(() => emptyState());
  }

  private update(change: (state: CheckoutState) => CheckoutState): void {
    const next = change(this.state());
    if (next === this.state()) {
      return;
    }
    this.state.set(next);
    try {
      this.storage?.setItem(CHECKOUT_STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Storage full or blocked (private mode): the checkout still works until a refresh.
    }
  }

  private restore(): CheckoutState {
    try {
      const saved = this.storage?.getItem(CHECKOUT_STORAGE_KEY);
      const parsed: unknown = saved ? JSON.parse(saved) : null;
      if (isCheckoutState(parsed)) {
        return parsed;
      }
    } catch {
      // Unreadable or corrupt: start a fresh checkout.
    }
    return emptyState();
  }
}

function emptyState(): CheckoutState {
  return { checkoutKey: newCheckoutKey(), address: null, shippingMethod: null, orderNumber: null };
}

/** An entry before the order exists keeps the key; one after it starts a new checkout. */
function afterChange(state: CheckoutState): CheckoutState {
  return state.orderNumber ? { ...state, checkoutKey: newCheckoutKey(), orderNumber: null } : state;
}

function sameAddress(a: ShippingAddress | null, b: ShippingAddress): boolean {
  return !!a && (Object.keys(b) as (keyof ShippingAddress)[]).every((key) => a[key] === b[key]);
}

function storageOf(document: Document): Storage | null {
  try {
    return document.defaultView?.sessionStorage ?? null;
  } catch {
    return null;
  }
}

/** A random v4 UUID; `randomUUID` needs a secure context, `getRandomValues` does not. */
export function newCheckoutKey(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function isCheckoutState(value: unknown): value is CheckoutState {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const state = value as Partial<CheckoutState>;
  return (
    typeof state.checkoutKey === 'string' &&
    state.checkoutKey.length > 0 &&
    (state.address === null || isAddress(state.address)) &&
    (state.shippingMethod === null ||
      state.shippingMethod === 'standard' ||
      state.shippingMethod === 'express') &&
    (state.orderNumber === null || typeof state.orderNumber === 'string')
  );
}

function isAddress(value: unknown): value is ShippingAddress {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const address = value as Record<string, unknown>;
  return (
    ['name', 'line1', 'city', 'region', 'postalCode', 'country'].every(
      (key) => typeof address[key] === 'string',
    ) &&
    (address['line2'] === null || typeof address['line2'] === 'string')
  );
}
