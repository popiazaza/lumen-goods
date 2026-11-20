import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ProductDto } from '../../core/api/products.api';
import { AvailabilityBadge } from '../../shared/products/availability-badge';
import { ProductImage } from '../../shared/products/product-image';
import { ProductPrice } from '../../shared/products/product-price';

/**
 * One product in the catalogue grid. The name is the only link (one tab stop per card); its
 * `::after` stretches over the card so the whole card is clickable.
 */
@Component({
  selector: 'app-product-card',
  imports: [RouterLink, AvailabilityBadge, ProductImage, ProductPrice],
  template: `
    <app-product-image class="image" [src]="imageUrl()" />
    <div class="body">
      <h3 class="name">
        <a [routerLink]="['/products', product().id]">{{ product().name }}</a>
      </h3>
      <p class="seller">Sold by {{ sellerName() }}</p>
      <app-product-price class="price-line" [price]="product().price" />
      <app-availability-badge [product]="product()" />
    </div>
  `,
  styles: `
    :host {
      position: relative;
      display: flex;
      flex-direction: column;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--lg-radius-card);
      background: var(--mat-sys-surface-container-lowest);
      overflow: hidden;
      transition: box-shadow 150ms ease;

      &:hover {
        box-shadow: var(--mat-sys-level2);
      }

      &:focus-within {
        outline: 3px solid var(--mat-sys-primary);
        outline-offset: 2px;
      }
    }

    // The card is not sellable: grey the photo, keep the text at full contrast.
    :host(.unavailable) .image {
      filter: grayscale(1);
      opacity: 0.6;
    }

    .image {
      border-radius: 0;
    }

    .body {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: var(--lg-space-1);
      padding: var(--lg-space-3);
    }

    .name {
      margin: 0;
      font: var(--mat-sys-title-small);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;

      a {
        color: var(--mat-sys-on-surface);
        text-decoration: none;

        // The card outline shows focus.
        &:focus-visible {
          outline: none;
        }

        &::after {
          content: '';
          position: absolute;
          inset: 0;
        }
      }
    }

    .seller {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .price-line {
      margin-top: auto;
      padding-top: var(--lg-space-1);
      font: var(--mat-sys-title-medium);
    }
  `,
  host: {
    '[class.unavailable]': '!product().sellable',
  },
})
export class ProductCard {
  readonly product = input.required<ProductDto>();
  /** The shop's display name, from `GET /sellers`. */
  readonly sellerName = input.required<string>();

  protected readonly imageUrl = computed(() => {
    const images = this.product().images;
    return (images.find((image) => image.isPrimary) ?? images[0])?.url ?? null;
  });
}
