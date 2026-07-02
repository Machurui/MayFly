# Build context: repo root (docker-compose uses context: .)
# Stage 1: build Vue SPA
FROM node:22-alpine AS build
WORKDIR /app
COPY MayFly.Web/package*.json ./
RUN npm ci
COPY MayFly.Web/ ./
RUN npm run build

# Stage 2: Caddy file-server for the compiled dist
FROM caddy:2-alpine
COPY --from=build /app/dist /srv
# SPA Caddyfile: serve /srv with index.html fallback for client-side routing
COPY MayFly.Web/Caddyfile /etc/caddy/Caddyfile
EXPOSE 80
