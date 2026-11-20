import { convertToParamMap } from '@angular/router';
import {
  CatalogueQuery,
  DEFAULT_CATALOGUE_QUERY,
  activeFilterCount,
  applyChange,
  catalogueQueryParams,
  clearFilters,
  differsOnlyInPage,
  pageWindow,
  parseCatalogueQuery,
  parsePrice,
  restrictSellers,
  toProductSearch,
} from './catalogue-query';

/** Parses a raw query string the way the router would. */
function parse(search: string): CatalogueQuery {
  const params: Record<string, string[]> = {};
  for (const [key, value] of new URLSearchParams(search)) {
    (params[key] ??= []).push(value);
  }
  return parseCatalogueQuery(convertToParamMap(params));
}

const FULL: CatalogueQuery = {
  q: 'tote',
  categories: ['apparel', 'bags'],
  sellers: ['harbor', 'kestrel'],
  minPrice: 10,
  maxPrice: 49.5,
  inStock: true,
  sort: 'price_asc',
  page: 3,
};

describe('catalogue query (URL state)', () => {
  it('gives the defaults for an empty query string', () => {
    expect(parse('')).toEqual(DEFAULT_CATALOGUE_QUERY);
    expect(catalogueQueryParams(DEFAULT_CATALOGUE_QUERY)).toEqual({});
  });

  it('parses every supported param, including repeated category and seller', () => {
    const query = parse(
      'q=tote&category=apparel&category=bags&seller=harbor&seller=kestrel' +
        '&minPrice=10&maxPrice=49.50&inStock=true&sort=price_asc&page=3',
    );

    expect(query).toEqual(FULL);
  });

  it('serialises with repeated keys and leaves defaults out', () => {
    expect(catalogueQueryParams(FULL)).toEqual({
      q: 'tote',
      category: ['apparel', 'bags'],
      seller: ['harbor', 'kestrel'],
      minPrice: '10',
      maxPrice: '49.5',
      inStock: 'true',
      sort: 'price_asc',
      page: '3',
    });
    expect(
      catalogueQueryParams({ ...FULL, sort: 'newest', page: 1, inStock: false }),
    ).not.toHaveProperty('sort');
  });

  it('round-trips: parse(serialise(state)) gives the same state', () => {
    const states: CatalogueQuery[] = [
      DEFAULT_CATALOGUE_QUERY,
      FULL,
      { ...DEFAULT_CATALOGUE_QUERY, minPrice: 0, maxPrice: 0 },
      { ...DEFAULT_CATALOGUE_QUERY, q: 'café & co', sellers: ['lumen'] },
    ];
    for (const state of states) {
      const params = catalogueQueryParams(state);
      const search = new URLSearchParams();
      for (const [key, value] of Object.entries(params)) {
        for (const item of [value].flat()) {
          search.append(key, String(item));
        }
      }
      expect(parse(search.toString())).toEqual(state);
    }
  });

  it('ignores unknown and malformed values instead of failing', () => {
    const query = parse(
      'category=apparel&category=toys&category=apparel&seller=Not%20A%20Seller&seller=harbor' +
        '&seller=%3Cscript%3E&minPrice=abc&maxPrice=-5&inStock=yes&sort=cheapest&page=0',
    );

    expect(query).toEqual({
      ...DEFAULT_CATALOGUE_QUERY,
      categories: ['apparel'],
      sellers: ['harbor'],
    });
  });

  it('keeps any well-formed seller id: sellers are configuration on the API', () => {
    expect(parse('seller=north-fjord&seller=harbor&seller=north-fjord').sellers).toEqual([
      'harbor',
      'north-fjord',
    ]);
  });

  it('drops sellers GET /sellers does not list, but only once that list is known', () => {
    const query = parse('seller=acme&seller=harbor&category=bags');

    expect(restrictSellers(query, null)).toBe(query);
    expect(restrictSellers(query, new Set(['harbor', 'kestrel']))).toEqual({
      ...query,
      sellers: ['harbor'],
    });
    const listed = new Set(['acme', 'harbor']);
    expect(restrictSellers(query, listed)).toBe(query);
  });

  it.each(['page=-1', 'page=1.5', 'page=abc', 'page=', 'page=0'])(
    'treats %s as page 1',
    (search) => {
      expect(parse(search).page).toBe(1);
    },
  );

  it('drops an impossible price range (min above max)', () => {
    expect(parse('minPrice=50&maxPrice=10')).toMatchObject({ minPrice: null, maxPrice: null });
    expect(parse('minPrice=10&maxPrice=10')).toMatchObject({ minPrice: 10, maxPrice: 10 });
  });

  it('puts multi-value filters in canonical order so one view has one URL', () => {
    expect(parse('category=bags&category=apparel').categories).toEqual(['apparel', 'bags']);
    expect(parse('seller=kestrel&seller=harbor').sellers).toEqual(['harbor', 'kestrel']);
  });

  it('trims the search text and caps its length', () => {
    expect(parse('q=%20%20lamp%20').q).toBe('lamp');
    expect(parse(`q=${'a'.repeat(500)}`).q).toHaveLength(200);
  });

  it('resets to page 1 on any change of search, filter or sort', () => {
    const changed = applyChange(FULL, { categories: ['home', 'apparel'] });

    expect(changed.page).toBe(1);
    expect(changed.categories).toEqual(['apparel', 'home']);
    expect(catalogueQueryParams(changed)).not.toHaveProperty('page');
  });

  it('clears refinements but keeps the search text and sort', () => {
    expect(clearFilters(FULL)).toEqual({
      ...DEFAULT_CATALOGUE_QUERY,
      q: 'tote',
      sort: 'price_asc',
    });
  });

  it('counts the refinements that are on (price range counts once)', () => {
    expect(activeFilterCount(DEFAULT_CATALOGUE_QUERY)).toBe(0);
    expect(activeFilterCount(FULL)).toBe(6);
  });

  it('builds the API request with the page size and an explicit sort', () => {
    expect(toProductSearch(DEFAULT_CATALOGUE_QUERY)).toMatchObject({
      q: undefined,
      sort: 'newest',
      page: 1,
      pageSize: 24,
    });
  });

  it('tells a page change from a filter change', () => {
    expect(differsOnlyInPage(FULL, { ...FULL, page: 4 })).toBe(true);
    expect(differsOnlyInPage(FULL, { ...FULL, page: 1, inStock: false })).toBe(false);
    expect(differsOnlyInPage(FULL, FULL)).toBe(false);
  });
});

describe('parsePrice', () => {
  it.each([
    ['10', 10],
    ['10.5', 10.5],
    ['10.50', 10.5],
    ['$25', 25],
    [' 7 ', 7],
  ])('reads %j as %d', (input, expected) => {
    expect(parsePrice(input)).toBe(expected);
  });

  it.each(['', '-1', '1e3', '10.555', 'ten', '1,000', null])('rejects %j', (input) => {
    expect(parsePrice(input)).toBeNull();
  });
});

describe('pageWindow', () => {
  it('lists every page when there are few', () => {
    expect(pageWindow(1, 1)).toEqual([1]);
    expect(pageWindow(2, 5)).toEqual([1, 2, 3, 4, 5]);
  });

  it('keeps the ends and two pages either side of the current one', () => {
    expect(pageWindow(10, 20)).toEqual([1, null, 8, 9, 10, 11, 12, null, 20]);
    expect(pageWindow(1, 20)).toEqual([1, 2, 3, null, 20]);
    expect(pageWindow(20, 20)).toEqual([1, null, 18, 19, 20]);
  });

  it('shows a single skipped page instead of an ellipsis', () => {
    expect(pageWindow(5, 20)).toEqual([1, 2, 3, 4, 5, 6, 7, null, 20]);
  });
});
