import { DOCUMENT } from '@angular/common';
import { Service, computed, inject, linkedSignal, signal } from '@angular/core';
import { PlaceOrderRequest } from '../api/checkout.api';
import { OrderDetailDto, ShippingAddress, ShippingMethod } from '../api/order.models';
import { SessionApi } from '../api/session.api';

export const CHECKOUT_STEPS = ['address', 'shipping', 'review', 'payment'] as const;
export type CheckoutStep = (typeof CHECKOUT_STEPS)[number];

/**
What the shopper has entered so far in this checkout.
*/
export interface CheckoutState {
  /**
   * Whose checkout this is: the signed-in email (lower case) when it was last saved, or null if
   * nobody was signed in. Another account in the same tab never sees it (see `ownedBy`).
   */
  owner: string | null;
  /**
  Sent with Place order, so a repeat of the same review returns the same order (BR-PAY-01).
  */
  checkoutKey: string;
  /**
   * The review the key was first sent with (`reviewFingerprint`), or null while it is unused. Saved
   * with the key, so a resubmit after a page refresh still counts as the same review.
   */
  keyReview: string | null;
  address: ShippingAddress | null;
  shippingMethod: ShippingMethod | null;
  /**
  Set once Place order succeeded: the order the payment step pays.
  */
  orderNumber: string | null;
}

/**
sessionStorage key: per tab, gone when the tab closes, never shared with other tabs.
*/
export const CHECKOUT_STORAGE_KEY = 'lumen.checkout.v1';

/**
 * The checkout's state across its four routes. A root signal store mirrored to sessionStorage,
 * so Back, the stepper and a page refresh all keep what the shopper entered (WCAG 3.3.7).
 * Stores no prices: the review and payment steps read them from the API. The one exception is Place order's own answer, kept in memory only, so the payment step can open on it without reading the order again; a refresh drops it and the page asks the API.
 */
@Service()
export class CheckoutStore {
  private readonly storage = storageOf(inject(DOCUMENT));

  private readonly sessionApi = inject(SessionApi);

  /**
  The signed-in account (lower-case email), or null while signed out or not known yet.
  */
  private readonly signedInAs = computed(() => {
    const { isAuthenticated, email } = this.sessionApi.session();
    return isAuthenticated && email ? email.toLowerCase() : null;
  });

  /**
   * Restored from storage on first read, and checked against the signed-in account whenever it changes, so a sign-in as someone else, a sign-out in another tab or an expired session never hands one customer's address and order to the next.
   */
  private readonly state = linkedSignal<string | null, CheckoutState>({
    source: this.signedInAs,
    computation: (account, previous) => this.ownedBy(account, previous?.value ?? this.restore()),
  });

  private readonly placed = signal<OrderDetailDto | null>(null);

  readonly checkoutKey = computed(() => this.state().checkoutKey);

  readonly address = computed(() => this.state().address);

  readonly shippingMethod = computed(() => this.state().shippingMethod);

  readonly orderNumber = computed(() => this.state().orderNumber);

  /**
  Place order's answer, while it is still this checkout's order (memory only).
  */
  readonly placedOrder = computed(() => {
    const order = this.placed();
    return order && order.number === this.orderNumber() ? order : null;
  });

  /**
  Steps the shopper may open now: done ones, plus the next one to do.
  */
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

  private update(change: (state: CheckoutState) => CheckoutState): void {
    const current = this.state();
    const changed = change(current);
    if (changed === current) {
      return;
    }
    const next = { ...changed, owner: this.signedInAs() ?? changed.owner };
    this.state.set(next);
    this.persist(next);
  }

  private persist(state: CheckoutState): void {
    if (this.storage) {
      save(this.storage, JSON.stringify(state));
    }
  }

  private restore(): CheckoutState {
    const saved = this.storage ? load(this.storage) : null;
    const parsed = saved ? parseJson(saved) : null;
    // State saved before keys were tied to a review has no `keyReview`: treat it as unused. State
    // saved before checkouts had an owner has none: nobody can say whose it is.
    return isCheckoutState(parsed)
      ? { ...parsed, keyReview: parsed.keyReview ?? null, owner: parsed.owner ?? null }
      : emptyState();
  }

  /**
   * `state` if it belongs to `account`, else a new checkout for `account` (saved over the old one,
   * so it is gone from this tab too). Signed out, the checkout is kept as it is: nobody can open
   * it (checkout needs a sign-in), and the same customer signing in again carries on.
   */
  private ownedBy(account: string | null, state: CheckoutState): CheckoutState {
    if (account === null || state.owner === account) {
      return state;
    }
    const fresh = { ...emptyState(), owner: account };
    // Not a signal write, so allowed here; the next read of storage must not find the old owner's data.
    this.persist(fresh);
    return fresh;
  }

  /**
   * A changed address after Place order leaves the placed order behind: it keeps its old address,
   * so the payment step closes until the order is placed again. The key changes then, because
   * the review does (`checkoutKeyFor`); the unpaid order expires on its own.
   */
  setAddress(address: ShippingAddress): void {
    this.update((state) =>
      isSameAddress(state.address, address) ? state : { ...afterChange(state), address }
    );
  }

  setShippingMethod(shippingMethod: ShippingMethod): void {
    this.update((state) =>
      state.shippingMethod === shippingMethod ? state : { ...afterChange(state), shippingMethod }
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
   * Place order succeeded: the payment step pays this order. The key and its review stay, so
   * placing the same review again (Payment, then the stepper's Review, then Place order) gets this
   * order back (BR-PAY-01a) instead of a second order that would reserve the own stock twice and
   * could leave the shopper with "Only 0 left". A different review gets a new key from
   * `checkoutKeyFor`; a paid or closed order ends the checkout with `reset`.
   */
  orderPlaced(order: OrderDetailDto): void {
    this.placed.set(order);
    this.update((state) => ({ ...state, orderNumber: order.number }));
  }

  /**
   * A new checkout key, keeping what was entered. For a key the API refused as another
   * customer's (`checkout-key-taken`): sending that key again could never place this order.
   */
  renewCheckoutKey(): void {
    this.update((state) => ({ ...state, ...freshKey(), orderNumber: null }));
  }

  /**
   * Forgets this checkout (after payment, or on sign-out) and starts the next with a new key. A
   * different account signing in needs no call: the store starts over on its own.
   */
  reset(): void {
    this.placed.set(null);
    this.update(() => emptyState());
  }
}

/**
Everything Place order sends except the key: the review the key stands for.
*/
export type ReviewedOrder = Omit<PlaceOrderRequest, 'checkoutKey'>;

/**
 * One string per review, the same however the lines are ordered: two reviews match only if Place order would send the same lines, quantities, prices, total, address and method.
 */
export const reviewFingerprint = (review: ReviewedOrder): string => {
  const address = review.shippingAddress;
  return JSON.stringify([
    review.lines
      .map((line) => [line.productId, line.quantity, line.unitPrice] as const)
      .toSorted(([a], [b]) => Number(a > b) - Number(a < b)),
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
};

const freshKey = (): Pick<CheckoutState, 'checkoutKey' | 'keyReview'> => ({
  checkoutKey: newCheckoutKey(),
  keyReview: null,
});

const emptyState = (): CheckoutState => ({
  owner: null,
  ...freshKey(),
  address: null,
  shippingMethod: null,
  orderNumber: null,
});

/**
 * After Place order, a changed entry no longer matches the placed order, so payment closes until
 * the order is placed again. The key is left to `checkoutKeyFor`, which compares the whole review:
 * changing the method and back is still the first order's review, and gets that order back.
 */
const afterChange = (state: CheckoutState): CheckoutState =>
  state.orderNumber ? { ...state, orderNumber: null } : state;

const isSameAddress = (a: ShippingAddress | null, b: ShippingAddress): boolean =>
  !!a && (Object.keys(b) as (keyof ShippingAddress)[]).every((key) => a[key] === b[key]);

const storageOf = (document: Document): Storage | null => {
  const view = document.defaultView;
  if (!view) {
    return null;
  }
  try {
    return view.sessionStorage;
  } catch {
    // Blocked by the browser (cookies off): the checkout works without saving.
    return null;
  }
};

const save = (storage: Storage, value: string): void => {
  try {
    storage.setItem(CHECKOUT_STORAGE_KEY, value);
  } catch {
    // Storage full or blocked (private mode): the checkout still works until a refresh.
  }
};

const load = (storage: Storage): string | null => {
  try {
    return storage.getItem(CHECKOUT_STORAGE_KEY);
  } catch {
    // Unreadable: start a fresh checkout.
    return null;
  }
};

const parseJson = (text: string): unknown => {
  try {
    return JSON.parse(text);
  } catch {
    // Corrupt: start a fresh checkout.
    return null;
  }
};

/**
A random v4 UUID; `randomUUID` needs a secure context, `getRandomValues` does not.
*/
export const newCheckoutKey = (): string => {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  // eslint-disable-next-line unicorn/prefer-uint8array-hex -- Uint8Array#toHex (ES2026) is outside Angular 22's browser baseline
  const hex = Array.from(crypto.getRandomValues(new Uint8Array(16)), (byte) =>
    byte.toString(16).padStart(2, '0')
  ).join('');
  // RFC 9562 layout: version 4 is the 13th hex digit; the variant (10xx) makes the 17th 8, 9, a or b.
  const variant = ((Number.parseInt(hex.slice(16, 17), 16) % 4) + 8).toString(16);
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-${variant}${hex.slice(17, 20)}-${hex.slice(20)}`;
};

const SAVED_SHIPPING_METHODS: ReadonlySet<unknown> = /* @__PURE__ */ new Set([
  null,
  'standard',
  'express',
]);

const isNullOrString = (value: unknown): boolean => value === null || typeof value === 'string';

const isCheckoutState = (value: unknown): value is CheckoutState => {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const state = value as Partial<CheckoutState>;
  const hasKey = typeof state.checkoutKey === 'string' && state.checkoutKey.length > 0;
  const hasKeyReview = state.keyReview === undefined || isNullOrString(state.keyReview);
  const hasOwner = state.owner === undefined || isNullOrString(state.owner);
  const hasAddress = state.address === null || isAddress(state.address);
  return [
    hasKey,
    hasKeyReview,
    hasOwner,
    hasAddress,
    SAVED_SHIPPING_METHODS.has(state.shippingMethod),
    isNullOrString(state.orderNumber),
  ].every(Boolean);
};

const isAddress = (value: unknown): value is ShippingAddress => {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const address = value as Record<string, unknown>;
  return (
    ['name', 'line1', 'city', 'region', 'postalCode', 'country'].every(
      (key) => typeof address[key] === 'string'
    ) &&
    (address['line2'] === null || typeof address['line2'] === 'string')
  );
};
