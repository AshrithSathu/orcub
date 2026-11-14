FROM rust:slim-bookworm AS build
WORKDIR /app
COPY Cargo.toml Cargo.lock ./
COPY src ./src
COPY config ./config
COPY migrations ./migrations
COPY static ./static
ENV SQLX_OFFLINE=true
RUN cargo build --release --locked

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/* && useradd --system --uid 10001 app
COPY --from=build /app/target/release/orcub-backend /usr/local/bin/orcub-backend
USER 10001
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["orcub-backend"]
