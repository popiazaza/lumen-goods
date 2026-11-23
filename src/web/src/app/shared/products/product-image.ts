import { NgOptimizedImage } from '@angular/common';
import { Component, input, linkedSignal } from '@angular/core';

/**
 * A product image in a fixed square, with a neutral placeholder when there is no image or it
 * fails to load (A-20), so the grid never shifts or shows a broken-image icon. A partner image
 * can fail for a moment (the proxy answers 503 while Harbor's image budget is spent); a later
 * view creates a new component, which tries the image again.
 */
@Component({
  selector: 'app-product-image',
  imports: [NgOptimizedImage],
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
