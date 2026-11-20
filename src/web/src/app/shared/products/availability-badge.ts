import { Component, computed, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { ProductDto } from '../../core/api/products.api';
import { availabilityOf } from './availability';

/** "In stock", "Only 3 left", "Out of stock" or "Not available", with an icon. */
@Component({
  selector: 'app-availability-badge',
  imports: [MatIcon],
  template: `
    <mat-icon [svgIcon]="label().icon" aria-hidden="true" />
    <span>{{ label().text }}</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--lg-space-1);
      font: var(--mat-sys-label-large);
    }

    mat-icon {
      width: 18px;
      height: 18px;
      flex: none;
    }

    :host(.available) {
      color: var(--lg-status-success);
    }

    :host(.low) {
      color: var(--lg-status-warning);
    }

    :host(.out) {
      color: var(--mat-sys-error);
    }

    :host(.unavailable) {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  host: {
    '[class]': 'label().tone',
  },
})
export class AvailabilityBadge {
  readonly product = input.required<Pick<ProductDto, 'availability' | 'sellable'>>();

  protected readonly label = computed(() => availabilityOf(this.product()));
}
