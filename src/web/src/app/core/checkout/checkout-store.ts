import { DOCUMENT } from '@angular/common';
import { Service, computed, inject, signal } from '@angular/core';
import { PlaceOrderRequest } from '../api/checkout.api';
import { OrderDetailDto, ShippingAddress, ShippingMethod } from '../api/order.models';

export const CHECKOUT_STEPS = ['address', 'shipping', 'review', 'payment'] as const;
export type CheckoutStep = (typeof CHECKOUT_STEPS)[number];

/** What the shopper has entered so far in this checkout. */
export interface CheckoutState {
  /** Sent with Place order, so a repeat of the same review returns the same order (BR-PAY-01). */
  checkoutKey: string;
  /**
   * The review the key was first sent with (`reviewFingerprint`), or null while it is unused. Saved
   * with the key, so a resubmit after a page refresh still counts as the same review.
   */
  keyReview: string | null;
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
 * Stores no prices: the review and payment steps read them from the API. The one exception is
 * Place order's own answer, kept in memory only, so the payment step can open on it without
 * reading the order again; a refresh drops it and the page asks the API.
 */
@Service()
export class CheckoutStore {
  private readonly storage = storageOf(inject(DOCUMENT));
  private readonly state = signal<CheckoutState>(this.restore());
  private readonly placed = signal<OrderDetailDto | null>(null);

  readonly checkoutKey = computed(() => this.state().checkoutKey);
  readonly address = computed(() => this.state().address);
  readonly shippingMethod = computed(() => this.state().shippingMethod);
  readonly orderNumber = computed(() => this.state().orderNumber);
  /** Place order's answer, while it is still this checkout's order (memory only). */
  readonly placedOrder = computed(() => {
    const order = this.placed();
    return order && order.number === this.orderNumber() ? order : null;
  });

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

  /**
   * The key to send with Place order for this review. The API answers a key it has seen with the
   * order that key made, whatever else the request says; so a key is only sent again for exactly
   * the same review (lines, quantities, prices, total, address, method), as when the shopper
   * resubmits after a network error and the first attempt may have made the order. A review that
   * differs in anything gets a new key, so it can never be answered with an earlier order.
   */
  checkoutKeyFor(review: ReviewedOrder): string {
    const fingerprint = reviewFingerprint(review);
    const { checkoutKey, keyReview } = this.state();
    if (keyReview === fingerprint) {
      return checkoutKey;
    }
    if (keyReview === null) {
      this.update((state) => ({ ...state, keyReview: fingerprint }));
      return checkoutKey;
    }
    const renewed = newCheckoutKey();
    this.update((state) => ({ ...state, checkoutKey: renewed, keyReview: fingerprint }));
    return renewed;
  }

  /**
   * Place order succeeded: the payment step pays this order. The key has made its order, so the
   * next Place order (the shopper went back and changed the cart, or this order expired unpaid)
   * starts with a new one instead of getting this order back.
   */
  orderPlaced(order: OrderDetailDto): void {
    this.placed.set(order);
    this.update((state) => ({ ...state, ...freshKey(), orderNumber: order.number }));
  }

  /**
   * A new checkout key, keeping what was entered. For a key the API refused as another
   * customer's (`checkout-key-taken`): sending that key again could never place this order.
   */
  renewCheckoutKey(): void {
    this.update((state) => ({ ...state, ...freshKey(), orderNumber: null }));
  }

  /** Forgets this checkout (after payment, or on sign-out) and starts the next with a new key. */
  reset(): void {
    this.placed.set(null);
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
        // State saved before keys were tied to a review has no `keyReview`: treat it as unused.
        return { ...parsed, keyReview: parsed.keyReview ?? null };
      }
    } catch {
      // Unreadable or corrupt: start a fresh checkout.
    }
    return emptyState();
  }
}

/** Everything Place order sends except the key: the review the key stands for. */
export type ReviewedOrder = Omit<PlaceOrderRequest, 'checkoutKey'>;

/**
 * One string per review, the same however the lines are ordered: two reviews match only if
 * Place order would send the same lines, quantities, prices, total, address and method.
 */
export function reviewFingerprint(review: ReviewedOrder): string {
  const address = review.shippingAddress;
  return JSON.stringify([
    [...review.lines]
      .map((line) => [line.productId, line.quantity, line.unitPrice] as const)
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)),
    review.expectedTotal,
    review.shippingMethod,
    [
      address.name,
      address.line1,
      address.line2,
      address.city,
      address.region,
      address.postalCode,
      address.country,
    ],
  ]);
}

function freshKey(): Pick<CheckoutState, 'checkoutKey' | 'keyReview'> {
  return { checkoutKey: newCheckoutKey(), keyReview: null };
}

function emptyState(): CheckoutState {
  return { ...freshKey(), address: null, shippingMethod: null, orderNumber: null };
}

/**
 * Before the order exists an entry changes nothing else (`checkoutKeyFor` compares the review when
 * it is placed); after it, the entry starts a new checkout, since the placed order keeps the old one.
 */
function afterChange(state: CheckoutState): CheckoutState {
  return state.orderNumber ? { ...state, ...freshKey(), orderNumber: null } : state;
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
    (state.keyReview === undefined ||
      state.keyReview === null ||
      typeof state.keyReview === 'string') &&
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
