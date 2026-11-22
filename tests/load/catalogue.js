// Load test of the storefront's read path: catalogue pages, search, filters and product pages, sent through nginx
// to the API the way a browser reaches them. It answers "how many catalogue requests a second does one API
// instance serve, and at what p95?" for DESIGN.md §7, whose estimates say 90 % of traffic is catalogue reads.
//
// Left out on purpose:
// - Checkout and payment: their rate limits (10 a minute per customer) would only measure 429s.
// - Partner images: Harbor's own limit (10 a minute) caps them, not Lumen.
//
// Against a running stack (docker compose up --build -d --wait):
//   docker compose run --rm k6                                 # 300 requests/s for 1 minute
//   docker compose run --rm -e RATE=1000 -e DURATION=2m k6     # another rate or length
//   docker compose run --rm -e BASE_URL=https://<staging host> -e RATE=400 k6   # a deployed instance (DESIGN §8)
//
// Every iteration is one request, so RATE is requests per second. A run fails (exit code 99) when p95 goes over
// 200 ms (DESIGN §7's trigger for scaling the catalogue), more than 1 % of requests fail, or more than 0.1 % could
// not be sent on time.

import { check, fail } from 'k6';
import http from 'k6/http';

const COMPOSE_URL = 'http://web:8080';
const BASE_URL = __ENV.BASE_URL || COMPOSE_URL;
const RATE = Number(__ENV.RATE || 300);
const DURATION = __ENV.DURATION || '1m';

// nginx answers only for localhost and 127.0.0.1 (its DNS rebinding guard), and so does the API (AllowedHosts),
// so a request to the compose name "web" must still say it is for localhost. Any other BASE_URL keeps its own Host.
const HOST = __ENV.HOST_HEADER || (BASE_URL === COMPOSE_URL ? 'localhost' : '');
const HEADERS = { Accept: 'application/json', 'Accept-Encoding': 'gzip', ...(HOST ? { Host: HOST } : {}) };

const CATEGORIES = ['apparel', 'footwear', 'bags', 'home', 'outdoor', 'accessories'];
const SORTS = ['newest', 'price_asc', 'price_desc', 'name_asc'];

// What one visitor's calls look like, in requests out of 100: mostly listing and product pages, with the app's
// start-up calls (session, cart, sellers) once per visit of several pages.
const MIX = [
  ['catalogue', 30],
  ['product', 27],
  ['search', 20],
  ['filter', 15],
  ['sellers', 4],
  ['session', 2],
  ['cart', 2],
];
const MIX_TOTAL = MIX.reduce((sum, [, weight]) => sum + weight, 0);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    browse: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      // Started up front: a VU added mid-run takes a moment, and requests due meanwhile are dropped.
      preAllocatedVUs: Math.max(50, Math.ceil(RATE / 10)),
      // Enough for every request of one second to wait at once; past that, k6 reports dropped_iterations.
      maxVUs: Math.max(200, RATE),
    },
  },
  thresholds: {
    'http_req_failed{scenario:browse}': ['rate<0.01'],
    'http_req_duration{scenario:browse}': ['p(95)<200'],
    // One line each in the summary, to see which request is slow.
    ...Object.fromEntries(MIX.map(([name]) => [`http_req_duration{name:${name}}`, ['p(95)<200']])),
    // Requests k6 could not send on time because every VU was waiting: under 0.1 % of the rate.
    dropped_iterations: [`rate<${RATE / 1000}`],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

// Real product ids and search words from the catalogue, plus a few warm-up calls so the first measured seconds
// don't include the API's start-up work (JIT and the EF Core model).
export function setup() {
  const res = http.get(`${BASE_URL}/api/v1/products?pageSize=96`, { headers: HEADERS, responseType: 'text' });
  if (res.status !== 200) {
    fail(`GET /api/v1/products answered ${res.status}. Is the stack up (docker compose up -d --wait)?`);
  }
  const products = res.json('items');
  if (products.length === 0) {
    fail('The catalogue is empty: wait for the first partner sync, then run again.');
  }

  const words = new Set();
  for (const product of products) {
    for (const word of product.name.toLowerCase().split(/[^a-z]+/)) {
      if (word.length >= 4) {
        words.add(word);
      }
    }
  }
  const data = { ids: products.map((p) => p.id), words: [...words] };

  for (let i = 0; i < 5; i++) {
    for (const [name] of MIX) {
      http.get(urlFor(name, data), { headers: HEADERS });
    }
  }
  return data;
}

export default function (data) {
  const name = pickRequest();
  const res = http.get(urlFor(name, data), { headers: HEADERS, tags: { name } });
  check(res, { 'status is 200': (r) => r.status === 200 });
}

function pickRequest() {
  let roll = Math.random() * MIX_TOTAL;
  for (const [name, weight] of MIX) {
    roll -= weight;
    if (roll < 0) {
      return name;
    }
  }
  return MIX[0][0];
}

function urlFor(name, data) {
  const api = `${BASE_URL}/api/v1`;
  switch (name) {
    case 'catalogue':
      return `${api}/products?sort=${pick(SORTS)}&page=${1 + Math.floor(Math.random() * 4)}`;
    case 'product':
      return `${api}/products/${pick(data.ids)}`;
    case 'search':
      return `${api}/products?q=${encodeURIComponent(pick(data.words))}`;
    case 'filter':
      return `${api}/products?category=${pick(CATEGORIES)}&inStock=true&maxPrice=${50 + Math.floor(Math.random() * 200)}&sort=price_asc`;
    case 'sellers':
      return `${api}/sellers`;
    case 'session':
      return `${api}/auth/session`;
    case 'cart':
      return `${api}/cart`;
    default:
      throw new Error(`Unknown request ${name}`);
  }
}

function pick(items) {
  return items[Math.floor(Math.random() * items.length)];
}
