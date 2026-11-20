import { Component, computed, effect, input, output } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  FormGroupDirective,
  NgForm,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatError, MatFormField, MatLabel, MatPrefix } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import {
  PRODUCT_CATEGORIES,
  PRODUCT_SOURCES,
  ProductCategory,
  ProductSource,
} from '../../core/api/products.api';
import { Seller } from '../../core/api/sellers.api';
import { categoryLabel } from '../../shared/products/product-labels';
import { CatalogueChange, CatalogueQuery, parsePrice } from './catalogue-query';

function priceValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value.trim();
  return value === '' || parsePrice(value) !== null ? null : { price: true };
}

function rangeValidator(group: AbstractControl): ValidationErrors | null {
  const min = parsePrice(group.get('min')?.value);
  const max = parsePrice(group.get('max')?.value);
  return min !== null && max !== null && min > max ? { range: true } : null;
}

/** Shows the max field in error for the group's min > max error too, once the form is submitted. */
class RangeErrorStateMatcher implements ErrorStateMatcher {
  isErrorState(control: AbstractControl | null, form: FormGroupDirective | NgForm | null): boolean {
    const invalid = !!control?.invalid || !!control?.parent?.hasError('range');
    return invalid && (!!control?.touched || !!form?.submitted);
  }
}

/**
 * The catalogue's refinements: category, seller, price range and "In stock only". Shows the state
 * it is given and emits what the shopper changed; the page turns that into a navigation. Used in
 * the desktop sidebar and in the mobile bottom sheet.
 */
@Component({
  selector: 'app-catalogue-filters',
  imports: [
    ReactiveFormsModule,
    MatButton,
    MatCheckbox,
    MatError,
    MatFormField,
    MatInput,
    MatLabel,
    MatPrefix,
    MatSlideToggle,
  ],
  templateUrl: './catalogue-filters.html',
  styleUrl: './catalogue-filters.scss',
})
export class CatalogueFilters {
  readonly query = input.required<CatalogueQuery>();
  readonly sellers = input.required<readonly Seller[]>();
  readonly filtersChange = output<CatalogueChange>();

  protected readonly categories = PRODUCT_CATEGORIES;
  protected readonly categoryLabel = categoryLabel;
  protected readonly rangeMatcher = new RangeErrorStateMatcher();

  /** Only sellers whose source the API's `seller` filter accepts. */
  protected readonly sellerOptions = computed(() =>
    this.sellers().filter((seller): seller is Seller & { source: ProductSource } =>
      (PRODUCT_SOURCES as readonly string[]).includes(seller.source),
    ),
  );

  protected readonly priceForm = new FormGroup(
    {
      min: new FormControl('', { nonNullable: true, validators: priceValidator }),
      max: new FormControl('', { nonNullable: true, validators: priceValidator }),
    },
    { validators: rangeValidator },
  );

  private readonly priceRange = computed(
    () => ({ min: this.query().minPrice, max: this.query().maxPrice }),
    { equal: (a, b) => a.min === b.min && a.max === b.max },
  );

  constructor() {
    // The URL owns the applied range (Back, shared links, chip removal). Re-sync the fields only
    // when it changes, so a half-typed range survives ticking a checkbox.
    effect(() => {
      const { min, max } = this.priceRange();
      this.priceForm.reset({ min: min?.toString() ?? '', max: max?.toString() ?? '' });
    });
  }

  protected toggleCategory(category: ProductCategory, checked: boolean): void {
    const current = this.query().categories;
    this.filtersChange.emit({
      categories: checked ? [...current, category] : current.filter((c) => c !== category),
    });
  }

  protected toggleSeller(source: ProductSource, checked: boolean): void {
    const current = this.query().sellers;
    this.filtersChange.emit({
      sellers: checked ? [...current, source] : current.filter((s) => s !== source),
    });
  }

  protected setInStock(inStock: boolean): void {
    this.filtersChange.emit({ inStock });
  }

  protected applyPrice(): void {
    this.priceForm.markAllAsTouched();
    if (this.priceForm.invalid) {
      return;
    }
    const { min, max } = this.priceForm.getRawValue();
    this.filtersChange.emit({ minPrice: parsePrice(min), maxPrice: parsePrice(max) });
  }
}
