import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideAppIcons } from '../../core/icons/app-icons';
import { ProductImage } from './product-image';

const TOTE = '/media/0195e9a0-0000-7000-8000-000000000001/0?v=abc';
const SIDE = '/media/0195e9a0-0000-7000-8000-000000000001/1?v=def';

const host = (fixture: ComponentFixture<ProductImage>) => fixture.nativeElement as HTMLElement;

const view = async (alt: string) => {
  const fixture = TestBed.createComponent(ProductImage);
  fixture.componentRef.setInput('src', TOTE);
  fixture.componentRef.setInput('alt', alt);
  await fixture.whenStable();
  return fixture;
};

// What the browser does with the proxy's 503 (image budget spent) or 502 (partner failed).
const failToLoad = async (fixture: ComponentFixture<ProductImage>) => {
  host(fixture).querySelector('img')?.dispatchEvent(new Event('error'));
  await fixture.whenStable();
};

describe('ProductImage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideAppIcons()] });
  });

  it('shows a neutral placeholder that keeps the product name when the image fails', async () => {
    const fixture = await view('Waxed Canvas Tote');

    await failToLoad(fixture);

    const placeholder = host(fixture).querySelector('.placeholder');

    expect(host(fixture).querySelector('img')).toBeNull();
    expect(placeholder?.getAttribute('role')).toBe('img');
    expect(placeholder?.getAttribute('aria-label')).toBe('No image of Waxed Canvas Tote');
    // A calm monogram, hidden from screen readers: the label above says what is missing.
    expect(placeholder?.querySelector('.mark')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('loads the real image again on a later view of the same product', async () => {
    await failToLoad(await view('Waxed Canvas Tote'));

    const later = await view('Waxed Canvas Tote');
    const img = host(later).querySelector('img');

    expect(img?.getAttribute('src')).toBe(TOTE);
    expect(img?.getAttribute('alt')).toBe('Waxed Canvas Tote');
    expect(host(later).querySelector('.placeholder')).toBeNull();
  });

  it('tries a new image after a failed one in the same place (a gallery moving on)', async () => {
    const fixture = await view('Waxed Canvas Tote');
    await failToLoad(fixture);

    fixture.componentRef.setInput('src', SIDE);
    await fixture.whenStable();

    expect(host(fixture).querySelector('img')?.getAttribute('src')).toBe(SIDE);
  });

  it('hides the placeholder from assistive technology when a link already names the product', async () => {
    const fixture = await view('');

    await failToLoad(fixture);

    const placeholder = host(fixture).querySelector('.placeholder');

    expect(placeholder?.getAttribute('aria-hidden')).toBe('true');
    expect(placeholder?.getAttribute('aria-label')).toBeNull();
  });
});
