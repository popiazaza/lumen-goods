import { Component, ElementRef, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Footer } from './core/layout/footer/footer';
import { Header } from './core/layout/header/header';

/** App shell: skip link, sticky header, routed page, footer. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Header, Footer],
  template: `
    <a class="skip-link" href="#main-content" (click)="skipToContent($event)">
      Skip to main content
    </a>
    <app-header />
    <main #main id="main-content" class="page" tabindex="-1">
      <router-outlet />
    </main>
    <app-footer />
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 100vh;
    }

    .page {
      flex: 1;
      width: 100%;
      max-width: var(--lg-content-max);
      box-sizing: border-box;
      margin: 0 auto;
      padding: var(--lg-space-6) var(--lg-gutter);

      &:focus {
        outline: none;
      }
    }

    .skip-link {
      position: absolute;
      top: var(--lg-space-2);
      left: var(--lg-space-2);
      z-index: 100;
      padding: var(--lg-space-3) var(--lg-space-4);
      border-radius: 8px;
      background: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);
      font: var(--mat-sys-label-large);
      transform: translateY(-200%);

      &:focus {
        transform: none;
      }
    }
  `,
})
export class App {
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  // A plain "#main-content" href would resolve against <base href="/"> and leave the page.
  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.main().nativeElement.focus();
  }
}
