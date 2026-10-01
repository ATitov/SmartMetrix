# Infrastructure test images

CI builds MinIO from the pinned official upstream source because the old
`minio/minio` Docker Hub image is unavailable. The Dockerfile verifies the source
commit before building. It is a test image, not a production deployment image.

Before running Docker-backed tests locally:

```sh
docker build -t smartmetrix-minio-test:2025-07-23 -f tests/infrastructure/minio.Dockerfile .
SMARTMETRIX_RUN_INTEGRATION_TESTS=true dotnet test SmartMetrix.sln -c Release
```

`SMARTMETRIX_TEST_MINIO_IMAGE` overrides the local image name. Alternatively,
provide `SMARTMETRIX_TEST_MINIO`, `SMARTMETRIX_TEST_MINIO_ACCESS_KEY` and
`SMARTMETRIX_TEST_MINIO_SECRET_KEY` to use an already running test MinIO.
PostgreSQL waits for its final TCP server; the temporary initdb Unix-socket server
is insufficient for readiness.
