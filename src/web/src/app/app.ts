import { Component, ElementRef, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Footer } from './core/layout/footer/footer';
import { Header } from './core/layout/header/header';

/**
App shell: skip link, sticky header, routed page, footer.
*/
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Header, Footer],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  // A plain "#main-content" href would resolve against <base href="/"> and leave the page.
  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.main().nativeElement.focus();
  }
}
