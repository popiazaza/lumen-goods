import { NgOptimizedImage } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { NgIcon } from '@ng-icons/core';
import { OwnMediaFile } from '../../core/api/admin-products.api';

/**
 * The image library as toggle buttons: pressing one adds the image to the product or takes it off again.
 * Once the product has the most images it can, only chosen ones can be pressed.
 */
@Component({
  selector: 'app-image-library',
  imports: [NgOptimizedImage, NgIcon],
  templateUrl: './image-library.html',
  styleUrl: './image-library.scss',
})
export class ImageLibrary {
  readonly images = input.required<readonly OwnMediaFile[]>();

  /**
  The files the product has now.
  */
  readonly chosen = input.required<readonly string[]>();

  readonly maxImages = input.required<number>();

  readonly toggled = output<string>();

  protected readonly chosenFiles = computed(() => new Set(this.chosen()));

  protected readonly isFull = computed(() => this.chosen().length >= this.maxImages());
}
