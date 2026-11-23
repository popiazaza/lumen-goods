import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideAppIcons } from '../../core/icons/app-icons';
import { CheckoutSteps } from './checkout-steps';

describe('CheckoutSteps', () => {
  let fixture: ComponentFixture<CheckoutSteps>;

  const host = () => fixture.nativeElement as HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    fixture = TestBed.createComponent(CheckoutSteps);
    fixture.componentRef.setInput('current', 'review');
    await fixture.whenStable();
  });

  afterEach(() => {
    fixture.destroy();
    sessionStorage.clear();
  });

  // On a phone only the current step's label shows (checkout-steps.scss hides the others
  // visually); every step keeps its label in the markup, so each still has a name.
  it('keeps every step label in the markup and marks the current step', () => {
    const steps = [...host().querySelectorAll('li')].map((li) => ({
      label: li.querySelector('.label')?.textContent?.trim(),
      current: li.classList.contains('current'),
    }));

    expect(steps).toStrictEqual([
      { label: 'Address', current: false },
      { label: 'Shipping', current: false },
      { label: 'Review', current: true },
      { label: 'Payment', current: false },
    ]);
    expect(host().querySelector('[aria-current="step"]')?.textContent).toContain('Review');
  });
});
