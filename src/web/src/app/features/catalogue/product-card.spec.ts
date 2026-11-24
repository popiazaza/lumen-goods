import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ProductDto } from '../../core/api/products.api';
import { provideAppIcons } from '../../core/icons/app-icons';
import { aProduct } from '../../testing/fixtures';
import { ProductCard } from './product-card';

const text = (host: HTMLElement, selector: string) =>
  host.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();

describe('ProductCard', () => {
  let fixture: ComponentFixture<ProductCard>;

  const render = async (
    product: ProductDto,
    sellerName = 'Harbor Supply Co.'
  ): Promise<HTMLElement> => {
    fixture.componentRef.setInput('product', product);
    fixture.componentRef.setInput('sellerName', sellerName);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAppIcons()] });
    fixture = TestBed.createComponent(ProductCard);
  });

  it('links the name to the product page', async () => {
    const host = await render(aProduct({ id: 'harbor-HSC-10021', name: 'Waxed Canvas Tote' }));

    const link = host.querySelector<HTMLAnchorElement>(':scope h3 a');

    expect(link?.textContent?.trim()).toBe('Waxed Canvas Tote');
    expect(link?.getAttribute('href')).toBe('/products/harbor-HSC-10021');
  });

  it('names the shop a partner product comes from', async () => {
    const host = await render(aProduct({ source: 'kestrel' }), 'Kestrel Outfitters');

    expect(text(host, '.seller')).toBe('Sold by Kestrel Outfitters');
  });

  it('formats the price as en-US dollars with two decimals', async () => {
    let host = await render(aProduct({ price: { amount: 49.9, currency: 'USD' } }));

    expect(text(host, 'app-product-price')).toBe('$49.90');

    host = await render(aProduct({ price: { amount: 1234.5, currency: 'USD' } }));

    expect(text(host, 'app-product-price')).toBe('$1,234.50');
  });

  it('shows "Price on request" when there is no price', async () => {
    const host = await render(aProduct({ price: null, sellable: false }));

    expect(text(host, 'app-product-price')).toBe('Price on request');
  });

  it.each([
    [{ status: 'in_stock', quantity: 40 }, 'In stock'],
    [{ status: 'low_stock', quantity: 3 }, 'Only 3 left'],
    [{ status: 'out_of_stock', quantity: 0 }, 'Out of stock'],
  ] as const)('shows availability %j as "%s" with an icon', async (availability, label) => {
    const host = await render(aProduct({ availability }));

    expect(text(host, 'app-availability-badge')).toBe(label);
    expect(host.querySelector(':scope app-availability-badge ng-icon')).not.toBeNull();
  });

  it('dims a product that cannot be bought and says "Not available"', async () => {
    const host = await render(aProduct({ sellable: false }));

    expect(fixture.nativeElement.classList).toContain('unavailable');
    expect(text(host, 'app-availability-badge')).toBe('Not available');
  });

  it('uses the primary image, and a placeholder when it fails or is missing', async () => {
    let host = await render(
      aProduct({
        images: [
          { url: 'http://localhost/media/a.jpg', isPrimary: false },
          { url: 'http://localhost/media/b.jpg', isPrimary: true },
        ],
      })
    );
    const img = host.querySelector('img');

    expect(img?.getAttribute('src')).toBe('http://localhost/media/b.jpg');
    expect(img?.getAttribute('alt')).toBe('');

    img?.dispatchEvent(new Event('error'));
    await fixture.whenStable();

    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('.placeholder')).not.toBeNull();

    host = await render(aProduct({ images: [] }));

    expect(host.querySelector('.placeholder')).not.toBeNull();
  });
});
