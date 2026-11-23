import { Component, computed, input } from '@angular/core';
import { NgIcon } from '@ng-icons/core';
import { ProductDto } from '../../core/api/products.api';
import { availabilityOf } from './availability';

/**
"In stock", "Only 3 left", "Out of stock" or "Not available", with an icon.
*/
@Component({
  selector: 'app-availability-badge',
  imports: [NgIcon],
  template: `
    <ng-icon [name]="label().icon" />
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
