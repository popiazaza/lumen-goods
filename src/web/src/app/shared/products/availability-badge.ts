import { Component, computed, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { ProductDto } from '../../core/api/products.api';
import { availabilityOf } from './availability';

/**
"In stock", "Only 3 left", "Out of stock" or "Not available", with an icon.
*/
@Component({
  selector: 'app-availability-badge',
  imports: [MatIcon],
  template: `
    <mat-icon aria-hidden="true" [svgIcon]="label().icon" />
    <span>{{ label().text }}</span>
  `,
  styleUrl: './availability-badge.scss',
  host: {
    '[class]': 'label().tone',
  },
})
export class AvailabilityBadge {
  readonly product = input.required<Pick<ProductDto, 'availability' | 'sellable'>>();

  protected readonly label = computed(() => availabilityOf(this.product()));
}
