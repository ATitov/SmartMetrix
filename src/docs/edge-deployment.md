# Edge deployment on Jetson AGX Orin

The production profile in `deploy/edge` targets Linux ARM64 and keeps credentials outside images and Git. Install JetPack, Docker Engine with the Compose plugin, and NVIDIA Container Toolkit first. Validate GPU access with `docker run --rm --runtime nvidia ubuntu nvidia-smi` (or `tegrastats` on Jetson releases without `nvidia-smi`).

## Bootstrap and first start

1. Put a release in `/opt/smartmetrix/releases/<version>` and point `/opt/smartmetrix/current` to it.
2. Run `sudo bash deploy/edge/scripts/bootstrap.sh` from the release directory.
3. Replace every value in `/etc/smartmetrix/edge.env`; keep the file owned by root with mode `0600`. Set `VIDEO_GID`, `CAMERA_DEVICE`, model/data roots, registry and bind address in `/etc/smartmetrix/deployment.env` when they differ from defaults. The deploy script owns `/etc/smartmetrix/version.env`; do not edit it manually.
4. Ensure `/var/lib/smartmetrix` is on a dedicated filesystem or project-quota-controlled subvolume. Reserve at least 10% of the system disk for the OS.
5. Start with `sudo systemctl start smartmetrix-edge`; inspect with `systemctl status smartmetrix-edge`, `docker compose -f deploy/edge/compose.yml ps`, and `journalctl -u smartmetrix-edge`.

The unit is enabled at boot, Compose restarts containers, health checks preserve diagnostics when an SDK/device is absent, and the watchdog reports/restarts unhealthy workloads. The camera service receives only the configured video device, video group, read-only Jetson libraries, and NVIDIA runtime—not blanket privileged access.

Set `SMARTMETRIX_RIG_ID` in the deployment environment for the HTTP measurement pipeline.
Bootstrap creates writable `orchestrator` and `camera` data directories for checkpoints and capture receipts.
The default owner is the runtime image's app user (UID/GID 1654); set `SMARTMETRIX_APP_UID` /
`SMARTMETRIX_APP_GID` when using a different base image, and pass `SMARTMETRIX_DATA_ROOT` to bootstrap
when overriding the data root. Include these directories in site backups.
Hardware rectification, calibration and positioning prerequisites are documented in
[measurement-pipeline.md](measurement-pipeline.md); configuring a RigId alone does not enable a field-ready pipeline.

## Disk and offline retention

Container logs rotate at 3 × 10 MB per container. Also set journald limits in `/etc/systemd/journald.conf.d/smartmetrix.conf` (`SystemMaxUse=512M`, `SystemKeepFree=2G`) and restart journald. NATS has an 8 GB file-store ceiling. Configure MinIO lifecycle expiry for the operational retention window and monitor the dedicated data filesystem at 80/90%; do not place `/var/lib/smartmetrix` on the root filesystem without an enforced quota. Application object retention is controlled by `Storage__RetentionDays` in the secret environment file.

## Update and rollback

Build/push every service target listed in Compose with BuildKit, for example:

```bash
docker buildx build --platform linux/arm64 -f deploy/edge/Dockerfile \
  --build-arg SERVICE=SmartMetrix.ApiGateway \
  -t ghcr.io/atitov/smartmetrix-api:<version> --push .
```

Stage the complete release locally, pull images while connectivity is available, then run `sudo bash deploy/edge/scripts/deploy.sh /opt/smartmetrix/releases/<version> <version>`. The script atomically changes `current`, waits for healthy services, and restores the previous symlink if startup fails. For a manual rollback, repoint `/opt/smartmetrix/current`, restore the previous `SMARTMETRIX_VERSION` in `/etc/smartmetrix/version.env`, then restart the unit. Never use mutable `latest` tags for a field release.

## Backup and restore

Run `sudo bash deploy/edge/scripts/backup.sh /mnt/backup/smartmetrix` to capture PostgreSQL, MinIO objects, version metadata and checksums on separate media. Test restores regularly. To restore, stop the unit, verify `SHA256SUMS`, restore the MinIO archive into an empty data directory, start PostgreSQL, and run `pg_restore --clean --if-exists -U smartmetrix -d smartmetrix postgres.dump`; then deploy the matching application version.

NATS data is intentionally not backed up: durable business state lives in PostgreSQL/object storage, while JetStream is bounded offline transport. Secrets are backed up separately through the site's secret-management procedure and must never be copied into a release archive.
