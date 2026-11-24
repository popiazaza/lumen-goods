/**
 * jsdom lacks two browser APIs that spartan's popovers use (a select's listbox): ResizeObserver,
 * to size the list to its trigger, and `scrollIntoView`, to keep the active option in sight.
 */
class NoopResizeObserver {
  readonly observed = new Set<Element>();

  observe(target: Element): void {
    this.observed.add(target);
  }

  unobserve(target: Element): void {
    this.observed.delete(target);
  }

  disconnect(): void {
    this.observed.clear();
  }
}

/**
Sets `key` on `target` for a spec; the function it returns puts the original back.
*/
const stubProperty = (target: object, key: string, value: unknown): (() => void) => {
  const original = Object.getOwnPropertyDescriptor(target, key);
  Object.defineProperty(target, key, { configurable: true, writable: true, value });
  return () => {
    if (original) {
      Object.defineProperty(target, key, original);
    } else {
      Reflect.deleteProperty(target, key);
    }
  };
};

/**
Stubs both for a spec (in `beforeEach`); call the function it returns in `afterEach`.
*/
export const stubOverlayMeasuring = (): (() => void) => {
  const restorers = [stubProperty(globalThis, 'ResizeObserver', NoopResizeObserver)];
  if (!('scrollIntoView' in Element.prototype)) {
    restorers.push(stubProperty(Element.prototype, 'scrollIntoView', Function.prototype));
  }
  return () => {
    for (const restore of restorers) {
      restore();
    }
  };
};
