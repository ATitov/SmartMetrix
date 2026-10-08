# Локальный стенд ревью

Запуск: `./deploy/local/start-review-stand.ps1 -NoBuild` после Release build.
Скрипт повторно использует собственные процессы и отказывается занимать порты
другого запуска. Он подключается к существующим PostgreSQL/NATS/MinIO, не запускает
новые экземпляры инфраструктуры. HTTP слушает только 127.0.0.1.

Подключения читаются из игнорируемого
`artifacts/integration-stand/connections.local.json`. БД:
`smartmetrix_review_20260930_f4433d63`; NATS: `nats://127.0.0.1:14222`;
MinIO: `http://127.0.0.1:19000`, bucket `smartmetrix-demo-20260930`.
PostgreSQL persistence включён для восьми сервисов, которые поддерживают его;
остальные сервисы используют свою штатную модель хранения.

Интерфейс: http://127.0.0.1:5190/login/. Учётные записи `admin` и `engineer`;
локальные пароли находятся в
`artifacts/integration-stand/application/credentials.local.json`.
При переходе со старого workspace запуска пароли сохранены.
Инженер имеет роли engineer/operator и области demo-ekg-12, demo-ekg-15,
demo-ekg-20, соответствующие существующим идентификаторам тестового набора.

Автоматическая обработка (`Pipeline:Enabled`) и CaptureTrigger consumer отключены.
Настроен профиль Local, Camera=Arena, Depth=Cpu, Segmentation=StoneVision.
Готовность камеры зависит от установленного Arena backend и оборудования.
Readiness оркестратора возвращает 503 при отключённом pipeline.
Полный запуск нового измерения требует отдельной настройки и приёмки.

Процессы и их идентичность записаны в
`artifacts/integration-stand/application/*.pid.json`, stdout/stderr — в `logs/`.
Штатные start/stop из другого deployment не управляют этими процессами.

Проверено после запуска: 14 ответов /health, 12 ответов 200 /ready;
camera/orchestrator возвращают 503 по причинам выше. Инженерский scoped API
читает 8 измерений (3/3/2), результат с 10 блоками; StorageService скачивает
реальный JSON manifest из MinIO. JetStream SMARTMETRIX_REVIEW_STAND содержит
13 событий, отправленных PostgreSQL outbox.
