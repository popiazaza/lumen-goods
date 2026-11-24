# syntax=docker/dockerfile:1
# Lumen Goods storefront. Build context: src/web
#   docker build -t lumen-web:dev src/web

FROM node:26.13.1-alpine3.24 AS build
WORKDIR /app
ENV NG_CLI_ANALYTICS=false
# Dependencies first so this layer is cached until the lockfile changes.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginx/nginx-unprivileged:1.30.8-alpine3.24
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/lumen-web/browser /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
