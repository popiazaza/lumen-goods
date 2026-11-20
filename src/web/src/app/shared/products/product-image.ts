import { NgOptimizedImage } from '@angular/common';
import { Component, input, linkedSignal } from '@angular/core';
import { MatIcon } from '@angular/material/icon';

/**
 * A product image in a fixed square, with a neutral placeholder when there is no image or it
 * fails to load (A-20), so the grid never shifts or shows a broken-image icon.
 */
@Component({
  selector: 'app-product-image',
  imports: [NgOptimizedImage, MatIcon],
  templateUrl: './product-image.html',
  styleUrl: './product-image.scss',
})
export class ProductImage {
  readonly src = input<string | null | undefined>(null);
  /**
  Empty when the image sits inside a link that already names the product.
  */
  readonly alt = input('');
  /**
  Load at once (the product page's main image); otherwise lazy.
  */
  readonly eager = input(false);

  /**
  Reset for every new `src`, so a gallery can move on from a broken image.
  */
  protected readonly failed = linkedSignal({ source: this.src, computation: () => false });
}
