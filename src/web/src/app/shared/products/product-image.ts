import { Component, input, linkedSignal } from '@angular/core';
import { MatIcon } from '@angular/material/icon';

/**
 * A product image in a fixed square, with a neutral placeholder when there is no image or it
 * fails to load (A-20), so the grid never shifts or shows a broken-image icon.
 */
@Component({
  selector: 'app-product-image',
  imports: [MatIcon],
  template: `
    @if (src() && !failed()) {
      <img
        [src]="src()"
        [alt]="alt()"
        [attr.loading]="eager() ? 'eager' : 'lazy'"
        [attr.fetchpriority]="eager() ? 'high' : null"
        decoding="async"
        width="600"
        height="600"
        (error)="failed.set(true)"
      />
    } @else {
      <div
        class="placeholder"
        role="img"
        [attr.aria-label]="alt() ? 'No image of ' + alt() : null"
        [attr.aria-hidden]="alt() ? null : 'true'"
      >
        <mat-icon svgIcon="image" aria-hidden="true" />
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      aspect-ratio: 1;
      overflow: hidden;
      border-radius: var(--lg-radius-card);
      background: var(--mat-sys-surface-container);
    }

    img {
      display: block;
      width: 100%;
      height: 100%;
      object-fit: cover;
    }

    .placeholder {
      display: grid;
      place-items: center;
      width: 100%;
      height: 100%;
      color: var(--mat-sys-on-surface-variant);

      mat-icon {
        width: 48px;
        height: 48px;
      }
    }
  `,
})
export class ProductImage {
  readonly src = input<string | null | undefined>(null);
  /** Empty when the image sits inside a link that already names the product. */
  readonly alt = input('');
  /** Load at once (the product page's main image); otherwise lazy. */
  readonly eager = input(false);

  /** Reset for every new `src`, so a gallery can move on from a broken image. */
  protected readonly failed = linkedSignal({ source: this.src, computation: () => false });
}
