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
  templateUrl: './product-card.html',
  styleUrl: './product-card.scss',
  host: {
    '[class.unavailable]': '!product().sellable',
  },
})
export class ProductCard {
  readonly product = input.required<ProductDto>();
  /**
  The shop's display name, from `GET /sellers`.
  */
  readonly sellerName = input.required<string>();

  protected readonly imageUrl = computed(() => {
    const { images } = this.product();
    return (images.find((image) => image.isPrimary) ?? images[0])?.url ?? null;
  });
}
