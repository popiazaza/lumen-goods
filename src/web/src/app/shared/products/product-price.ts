import { CurrencyPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { Money } from '../../core/api/products.api';

/**
The API's price as en-US USD with two decimals ("$1,234.50"), or "Price on request".
*/
@Component({
  selector: 'app-product-price',
  imports: [CurrencyPipe],
  templateUrl: './product-price.html',
  styleUrl: './product-price.scss',
})
export class ProductPrice {
  readonly price = input.required<Money | null>();
}
