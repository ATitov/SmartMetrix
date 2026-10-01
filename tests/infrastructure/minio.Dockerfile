FROM golang:1.24.6-alpine AS build
RUN apk add --no-cache git
WORKDIR /src
RUN git clone --depth 1 --branch RELEASE.2025-07-23T15-54-02Z https://github.com/minio/minio.git . \
    && test "$(git rev-parse HEAD)" = "7ced9663e6a791fef9dc6be798ff24cda9c730ac"
RUN CGO_ENABLED=0 go build -mod=readonly -trimpath -ldflags="-s -w" -o /minio .

FROM alpine:3.22
RUN apk add --no-cache ca-certificates
COPY --from=build /minio /usr/local/bin/minio
EXPOSE 9000
ENTRYPOINT ["minio"]
