import { Component, computed, effect, input, output, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmSwitch } from '@spartan-ng/helm/switch';
import { PRODUCT_CATEGORIES, ProductCategory, ProductSource } from '../../core/api/products.api';
import { Seller } from '../../core/api/sellers.api';
import { categoryLabel } from '../../shared/products/product-labels';
import { CatalogueChange, CatalogueQuery, parsePrice } from './catalogue-query';

const priceValidator = (control: AbstractControl<string>): ValidationErrors | null => {
  const value = control.value.trim();
  return value === '' || parsePrice(value) !== null ? null : { price: true };
};

const rangeValidator = (group: AbstractControl): ValidationErrors | null => {
  const min = parsePrice(group.get('min')?.value);
  const max = parsePrice(group.get('max')?.value);
  return min !== null && max !== null && min > max ? { range: true } : null;
};

const PRICE_FORMAT_ERROR = 'Enter an amount like 25 or 25.50';

/**
 * The catalogue's refinements: category, seller, price range and "In stock only". Shows the state it is given and emits what the shopper changed; the page turns that into a navigation. Used in the desktop sidebar and in the mobile bottom sheet.
 */
@Component({
  selector: 'app-catalogue-filters',
  imports: [ReactiveFormsModule, HlmButton, HlmCheckbox, HlmInput, HlmLabel, HlmSwitch],
  templateUrl: './catalogue-filters.html',
  styleUrl: './catalogue-filters.scss',
})
export class CatalogueFilters {
  /**
  Errors show once a field is left or the range is applied, as Material's form fields did.
  */
  private readonly submitted = signal(false);

  private readonly priceRange = computed(
    () => ({ min: this.query().minPrice, max: this.query().maxPrice }),
    { equal: (a, b) => a.min === b.min && a.max === b.max }
  );

  readonly query = input.required<CatalogueQuery>();

  /**
  The seller checkboxes, from `GET /sellers` (the page decides what to show while it loads).
  */
  readonly sellers = input.required<readonly Seller[]>();

  /**
  Shown in the seller group when the list could not be loaded.
  */
  readonly sellersFailed = input(false);

  /**
  The sidebar and the phone sheet both show the filters: each copy needs its own field ids.
  */
  readonly idPrefix = input.required<string>();

  readonly filtersChange = output<CatalogueChange>();

  protected readonly categories = PRODUCT_CATEGORIES;

  protected readonly categoryLabel = categoryLabel;

  protected readonly ids = computed(() => {
    const id = `${this.idPrefix()}-price`;
    return {
      min: `${id}-min`,
      max: `${id}-max`,
      minError: `${id}-min-error`,
      maxError: `${id}-max-error`,
    };
  });

  protected readonly priceForm = new FormGroup(
    {
      min: new FormControl('', { nonNullable: true, validators: priceValidator }),
      max: new FormControl('', { nonNullable: true, validators: priceValidator }),
    },
    { validators: rangeValidator }
  );

  constructor() {
    // The URL owns the applied range (Back, shared links, chip removal). Re-sync the fields only
    // when it changes, so a half-typed range survives ticking a checkbox.
    effect(() => {
      const { min, max } = this.priceRange();
      this.priceForm.reset({ min: min?.toString() ?? '', max: max?.toString() ?? '' });
      this.submitted.set(false);
    });
  }

  private isShown(control: AbstractControl): boolean {
    return control.touched || this.submitted();
  }

  /**
  The min field's message, or null. Read on every check: form state is not a signal.
  */
  protected minError(): string | null {
    const control = this.priceForm.controls.min;
    return this.isShown(control) && control.hasError('price') ? PRICE_FORMAT_ERROR : null;
  }

  /**
  The max field's message: its own format error first, then the group's min > max error.
  */
  protected maxError(): string | null {
    const control = this.priceForm.controls.max;
    if (!this.isShown(control)) {
      return null;
    }
    if (control.hasError('price')) {
      return PRICE_FORMAT_ERROR;
    }
    return this.priceForm.hasError('range') ? 'Max must be at least the min' : null;
  }

  protected toggleCategory(category: ProductCategory, isChecked: boolean): void {
    const current = this.query().categories;
    this.filtersChange.emit({
      categories: isChecked ? [...current, category] : current.filter((c) => c !== category),
    });
  }

  protected toggleSeller(source: ProductSource, isChecked: boolean): void {
    const current = this.query().sellers;
    this.filtersChange.emit({
      sellers: isChecked ? [...current, source] : current.filter((s) => s !== source),
    });
  }

  protected setInStock(isInStock: boolean): void {
    this.filtersChange.emit({ inStock: isInStock });
  }

  protected applyPrice(): void {
    this.submitted.set(true);
    this.priceForm.markAllAsTouched();
    if (this.priceForm.invalid) {
      return;
    }
    const { min, max } = this.priceForm.getRawValue();
    this.filtersChange.emit({ minPrice: parsePrice(min), maxPrice: parsePrice(max) });
  }
}
