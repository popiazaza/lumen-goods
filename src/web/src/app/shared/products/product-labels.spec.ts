import { availabilityOf } from './availability';
import { categoryLabel, humaniseAttributeKey } from './product-labels';

describe('humaniseAttributeKey', () => {
  it.each([
    ['color', 'Color'],
    ['fillPower', 'Fill power'],
    ['heightCm', 'Height cm'],
    ['sizeEU', 'Size EU'],
    ['uvIndexRating', 'Uv index rating'],
    ['water_resistance', 'Water resistance'],
    ['', ''],
  ])('%j -> %j', (key, label) => {
    expect(humaniseAttributeKey(key)).toBe(label);
  });
});

describe('categoryLabel', () => {
  it('names each category', () => {
    expect(categoryLabel('accessories')).toBe('Accessories');
    expect(categoryLabel('home')).toBe('Home');
  });
});

const sellable = (status: 'in_stock' | 'low_stock' | 'out_of_stock', quantity: number | null) =>
  availabilityOf({ sellable: true, availability: { status, quantity } });

describe('availabilityOf', () => {
  it('pairs every state with an icon and text', () => {
    expect(sellable('in_stock', 12)).toStrictEqual({
      tone: 'available',
      icon: 'lucideCircleCheck',
      text: 'In stock',
    });
    expect(sellable('low_stock', 3)).toStrictEqual({
      tone: 'low',
      icon: 'lucideTriangleAlert',
      text: 'Only 3 left',
    });
    expect(sellable('low_stock', null).text).toBe('Low stock');
    expect(sellable('out_of_stock', 0)).toStrictEqual({
      tone: 'out',
      icon: 'lucideCircleX',
      text: 'Out of stock',
    });
  });

  it('says "Not available" for a product that cannot be bought, whatever its stock', () => {
    expect(
      availabilityOf({ sellable: false, availability: { status: 'in_stock', quantity: null } })
    ).toStrictEqual({ tone: 'unavailable', icon: 'lucideBan', text: 'Not available' });
  });
});
