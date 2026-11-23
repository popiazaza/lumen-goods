import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DatePipe } from '@angular/common';
import {
  Component,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { NgIcon } from '@ng-icons/core';
import { finalize } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { OWN_SOURCE } from '../../core/api/order.models';
import { httpStatusOf, problemMessage } from '../../core/api/problem-details';
import { ProductDto, ProductsApi } from '../../core/api/products.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { LastCatalogueQuery } from '../../core/catalogue/last-catalogue-query';
import { SITE_NAME } from '../../core/routing/page-title-strategy';
import { SellerDirectory } from '../../core/catalogue/seller-directory';
import { AvailabilityBadge } from '../../shared/products/availability-badge';
import { ProductImage } from '../../shared/products/product-image';
import { categoryLabel, humaniseAttributeKey } from '../../shared/products/product-labels';
import { ProductPrice } from '../../shared/products/product-price';
import { clampQuantity, maxSelectableQuantity } from './quantity';

/**
The tags the server's sanitiser keeps (A-21).
*/
const SANITISER_TAG = /<\/?(?:p|br|b|strong|i|em|ul|ol|li)\b[^>]*>/iu;

/**
Product detail (`/products/:id`): gallery, price, stock, description, attributes, Add to cart.
*/
@Component({
  selector: 'app-product-detail-page',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    NgIcon,
    HlmButton,
    HlmLabel,
    HlmSkeleton,
    AvailabilityBadge,
    ProductImage,
    ProductPrice,
  ],
  templateUrl: './product-detail-page.html',
  styleUrl: './product-detail-page.scss',
})
export class ProductDetailPage {
  private readonly productsApi = inject(ProductsApi);

  private readonly cart = inject(CartStateService);

  private readonly announcer = inject(LiveAnnouncer);

  private readonly title = inject(Title);

  protected readonly sellerDirectory = inject(SellerDirectory);

  protected readonly lastQuery = inject(LastCatalogueQuery);

  /**
  The `:id` route param (`withComponentInputBinding`).
  */
  readonly id = input.required<string>();

  protected readonly categoryLabel = categoryLabel;

  protected readonly product = rxResource({
    params: () => this.id(),
    stream: ({ params }) => this.productsApi.get(params),
  });

  protected readonly loaded = computed<ProductDto | undefined>(() =>
    this.product.hasValue() ? this.product.value() : undefined
  );

  protected readonly isNotFound = computed(() => httpStatusOf(this.product.error()) === 404);

  protected readonly isError = computed(() => this.product.status() === 'error');

  /**
  One `<h1>` for every state, so the focus the router puts on it survives the load.
  */
  protected readonly heading = computed(() => {
    const product = this.loaded();
    if (product) {
      return product.name;
    }
    return this.isNotFound() ? 'Product not found' : 'Product details';
  });

  /**
  Primary image first, then the rest in the API's order.
  */
  protected readonly images = computed(() => {
    const images = this.loaded()?.images ?? [];
    return [...images.filter((image) => image.isPrimary), ...images.filter((i) => !i.isPrimary)];
  });

  protected readonly selectedImage = linkedSignal({ source: this.images, computation: () => 0 });

  protected readonly mainImageAlt = computed(() => {
    const name = this.loaded()?.name ?? '';
    const total = this.images().length;
    return total > 1 ? `${name}, image ${this.selectedImage() + 1} of ${total}` : name;
  });

  /**
  A partner description is always the server sanitiser's output (A-21): HTML even when it has no tags, so a plain "&"
  arrives as "&amp;" and must be rendered as HTML. Lumen's own descriptions are the text an admin typed, shown as text.
  */
  protected readonly descriptionIsHtml = computed(() => {
    const product = this.loaded();
    return product !== undefined && product.source !== OWN_SOURCE;
  });

  /**
  A partner description with none of the sanitiser's tags is plain text, encoded: it still renders as HTML, but keeps its line breaks as an own description does.
  */
  protected readonly descriptionHasNoTags = computed(
    () => !SANITISER_TAG.test(this.loaded()?.description ?? '')
  );

  protected readonly attributes = computed(() =>
    Object.entries(this.loaded()?.attributes ?? {}).map(([key, value]) => ({
      key,
      value,
      label: humaniseAttributeKey(key),
    }))
  );

  protected readonly sellerName = computed(() => {
    const product = this.loaded();
    return product ? this.sellerDirectory.nameOf(product.source) : '';
  });

  protected readonly maxQuantity = computed(() => {
    const product = this.loaded();
    return product ? maxSelectableQuantity(product) : 0;
  });

  protected readonly canBuy = computed(() => this.maxQuantity() > 0);

  protected readonly quantityHint = computed(() => {
    const quantity = this.loaded()?.availability.quantity ?? null;
    const max = this.maxQuantity();
    return quantity !== null && quantity <= max ? `${max} available` : `Up to ${max} at a time`;
  });

  /**
  Why Add to cart is disabled, shown next to it and linked with `aria-describedby`.
  */
  protected readonly unavailableReason = computed(() => {
    const product = this.loaded();
    if (!product || this.canBuy()) {
      return '';
    }
    if (!product.sellable) {
      return product.price === null
        ? 'This product has no online price, so it can’t be bought here.'
        : 'This product is no longer available.';
    }
    return 'This product is out of stock right now.';
  });

  protected readonly buyForm = new FormGroup({
    quantity: new FormControl(1, { nonNullable: true }),
  });

  protected readonly quantity = toSignal(this.buyForm.controls.quantity.valueChanges, {
    initialValue: 1,
  });

  /**
  "Added to your cart", until the product (or its stock) is read again.
  */
  protected readonly addedMessage = linkedSignal({ source: this.loaded, computation: () => '' });

  /**
  A refused add stays explained after the product is re-read, until another product opens.
  */
  protected readonly addError = linkedSignal({ source: this.id, computation: () => '' });

  protected readonly adding = signal(false);

  constructor() {
    // The route title ("Product details") is generic; name the product once it is known (WCAG 2.4.2).
    effect(() => {
      const product = this.loaded();
      if (product) {
        this.title.setTitle(`${product.name} — ${SITE_NAME}`);
      } else if (this.isNotFound()) {
        this.title.setTitle(`Product not found — ${SITE_NAME}`);
      }
    });

    // A new product (or a stock change) starts the selector again at 1. An effect, not a
    // linkedSignal: the selector is a FormControl, which only takes values through its own API.
    effect(() => {
      const canBuy = this.canBuy();
      this.loaded();
      untracked(() => {
        const control = this.buyForm.controls.quantity;
        control.setValue(1);
        control[canBuy ? 'enable' : 'disable']();
      });
    });
  }

  protected step(delta: number): void {
    // The buttons stay focusable at a limit (aria-disabled, not disabled), so a keyboard user
    // keeps their place and clicks still arrive here.
    if (!this.canBuy()) {
      return;
    }
    const control = this.buyForm.controls.quantity;
    control.setValue(clampQuantity(control.value + delta, this.maxQuantity()));
  }

  /**
  Typed values outside 1..max snap back into range when the field loses focus.
  */
  protected normaliseQuantity(): void {
    const control = this.buyForm.controls.quantity;
    const clamped = clampQuantity(control.value, this.maxQuantity());
    if (clamped !== control.value) {
      control.setValue(clamped);
    }
  }

  protected addToCart(): void {
    const product = this.loaded();
    if (!product || !this.canBuy() || this.adding()) {
      return;
    }
    const quantity = clampQuantity(this.buyForm.controls.quantity.value, this.maxQuantity());
    this.buyForm.controls.quantity.setValue(quantity);
    this.addedMessage.set('');
    this.addError.set('');
    this.adding.set(true);
    this.cart
      .add(product.id, quantity)
      .pipe(finalize(() => this.adding.set(false)))
      .subscribe({
        next: (cart) => {
          this.addedMessage.set(quantity === 1 ? 'Added to cart.' : `Added ${quantity} to cart.`);
          const items = cart.itemCount === 1 ? '1 item' : `${cart.itemCount} items`;
          void this.announcer.announce(
            `Added to cart: ${quantity} × ${product.name}. Cart has ${items}.`,
            'polite'
          );
        },
        error: (error: unknown) => {
          // 422: no longer sellable, out of stock, or above 10 or the stock left (BR-CART-03).
          this.addError.set(problemMessage(error, 'We couldn’t add this to your cart. Try again.'));
          // The product may have changed; show its current stock and price.
          if (httpStatusOf(error) === 422) {
            this.product.reload();
          }
        },
      });
  }
}
