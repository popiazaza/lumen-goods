import { CurrencyPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { Money } from '../../core/api/products.api';

/** The API's price as en-US USD with two decimals ("$1,234.50"), or "Price on request". */
@Component({
  selector: 'app-product-price',
  imports: [CurrencyPipe],
  template: `
    @if (price(); as price) {
      <span class="price">{{
        price.amount | currency: price.currency : 'symbol' : '1.2-2' : 'en-US'
      }}</span>
    } @else {
      <span class="on-request">Price on request</span>
    }
  `,
  styles: `
    .on-request {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }
  `,
})
export class ProductPrice {
  readonly price = input.required<Money | null>();
}
