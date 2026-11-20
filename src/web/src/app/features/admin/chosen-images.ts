import { NgOptimizedImage } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';

/**
The images chosen for a product, in shop order: the first is the main one.
*/
@Component({
  selector: 'app-chosen-images',
  imports: [NgOptimizedImage, MatButton, MatIcon],
  templateUrl: './chosen-images.html',
  styleUrl: './chosen-images.scss',
})
export class ChosenImages {
  /**
  File names, the main image first.
  */
  readonly files = input.required<readonly string[]>();

  /**
  File name to URL; a file without one shows an empty thumbnail.
  */
  readonly imageUrls = input.required<ReadonlyMap<string, string>>();

  /**
  The file to show first in the shop.
  */
  readonly mainChosen = output<string>();

  readonly removed = output<string>();
}
