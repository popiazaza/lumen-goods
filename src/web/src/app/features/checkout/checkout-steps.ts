import { Component, computed, inject, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { CHECKOUT_STEPS, CheckoutStep, CheckoutStore } from '../../core/checkout/checkout-store';

const LABELS: Readonly<Record<CheckoutStep, string>> = {
  address: 'Address',
  shipping: 'Shipping',
  review: 'Review',
  payment: 'Payment',
};

/**
 * Checkout progress: a list of the four steps, URL-driven (each step is a route). Steps the
 * shopper may open are links; the current one has `aria-current="step"`; done ones say so in
 * text, not colour alone. Changing an earlier step after Place order starts a new order (the
 * store renews the checkout key); going back without changes returns the same one.
 */
@Component({
  selector: 'app-checkout-steps',
  imports: [RouterLink, MatIcon],
  template: `
    <nav aria-label="Checkout progress">
      <p class="compact">Step {{ index() + 1 }} of {{ steps.length }}</p>
      <ol>
        @for (step of items(); track step.id; let n = $index) {
          <li [class.current]="step.current" [class.done]="step.done">
            @if (step.link) {
              <a [routerLink]="['/checkout', step.id]">
                <span class="marker" aria-hidden="true">
                  @if (step.done) {
                    <mat-icon svgIcon="check_circle" />
                  } @else {
                    {{ n + 1 }}
                  }
                </span>
                <span>{{ step.label }}</span>
                @if (step.done) {
                  <span class="cdk-visually-hidden">(done)</span>
                }
              </a>
            } @else {
              <span class="step" [attr.aria-current]="step.current ? 'step' : null">
                <span class="marker" aria-hidden="true">{{ n + 1 }}</span>
                <span>{{ step.label }}</span>
              </span>
            }
          </li>
        }
      </ol>
    </nav>
  `,
  styles: `
    nav {
      margin: 0 0 var(--lg-space-4);
    }

    .compact {
      margin: 0 0 var(--lg-space-2);
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-large);
    }

    ol {
      display: flex;
      flex-wrap: wrap;
      gap: var(--lg-space-1) var(--lg-space-4);
      margin: 0;
      padding: 0;
      list-style: none;
    }

    a,
    .step {
      display: inline-flex;
      align-items: center;
      gap: var(--lg-space-2);
      min-height: 44px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-large);
    }

    a {
      color: var(--mat-sys-primary);
    }

    .current .step {
      color: var(--mat-sys-on-surface);
      font-weight: 700;
    }

    .marker {
      display: inline-grid;
      place-items: center;
      width: 24px;
      height: 24px;
      border: 2px solid currentColor;
      border-radius: 50%;
      font-size: 12px;

      mat-icon {
        width: 24px;
        height: 24px;
      }
    }

    .done .marker {
      border: 0;
    }

    .current .marker {
      background: var(--mat-sys-primary);
      border-color: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);
    }
  `,
})
export class CheckoutSteps {
  readonly current = input.required<CheckoutStep>();

  private readonly store = inject(CheckoutStore);
  protected readonly steps = CHECKOUT_STEPS;
  protected readonly index = computed(() => CHECKOUT_STEPS.indexOf(this.current()));

  protected readonly items = computed(() => {
    const reachable = this.store.reachable();
    const index = this.index();
    return CHECKOUT_STEPS.map((id, n) => {
      const current = n === index;
      return {
        id,
        label: LABELS[id],
        current,
        done: n < index,
        link: !current && reachable.has(id),
      };
    });
  });
}
