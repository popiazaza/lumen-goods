import { Component, ElementRef, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { HlmToaster } from '@spartan-ng/helm/sonner';
import { Footer } from './core/layout/footer/footer';
import { Header } from './core/layout/header/header';

/**
App shell: skip link, sticky header, routed page, footer.
*/
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Header, Footer, HlmToaster],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  /**
  The toaster in the theme's colours and type (sonner's own default font is the system one).
  */
  protected readonly toasterStyle = {
    '--normal-bg': 'var(--popover)',
    '--normal-text': 'var(--popover-foreground)',
    '--normal-border': 'var(--border)',
    '--border-radius': 'var(--radius)',
    '--brn-sonner-font-family': 'var(--font-sans)',
  };

  // A plain "#main-content" href would resolve against <base href="/"> and leave the page.
  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.main().nativeElement.focus();
  }
}
