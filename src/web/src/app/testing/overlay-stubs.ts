/**
 * jsdom lacks two browser APIs that spartan's popovers use (a select's listbox): ResizeObserver,
 * to size the list to its trigger, and `scrollIntoView`, to keep the active option in sight. Call
 * this in a spec's `beforeEach` before opening one, and `vi.unstubAllGlobals()` after.
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

export const stubOverlayMeasuring = (): void => {
  vi.stubGlobal('ResizeObserver', NoopResizeObserver);
  if (!('scrollIntoView' in Element.prototype)) {
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      value: Function.prototype,
    });
  }
};
