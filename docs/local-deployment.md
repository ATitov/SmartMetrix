# Локальное развёртывание SmartMetrix на Windows

Полный прикладной контур запускается как набор отдельных .NET-процессов. Локальные Windows-бинарники NATS JetStream и MinIO хранятся в игнорируемом Git каталоге `data/local-deployment/tools` и запускаются тем же скриптом. PostgreSQL/PostGIS зарезервирован архитектурой, но текущие реализации сервисов в локальном профиле его ещё не используют.

```powershell
.\deploy\local\deploy.ps1
C:\DEPLOY\SmartMetrix\start.ps1
C:\DEPLOY\SmartMetrix\status.ps1
C:\DEPLOY\SmartMetrix\stop.ps1
```

Операторская панель: `http://127.0.0.1:5190/operator/`, ключ `operator-secret`.
Инженерная панель: `http://127.0.0.1:5190/engineer/`, ключ `engineer-secret`.

Опубликованные сервисы, инфраструктурные бинарники и runtime-данные находятся в `C:\DEPLOY\SmartMetrix`. Структурированные JSONL-логи каждого сервиса записываются в отдельный каталог `C:\DEPLOY_LOG\SmartMetrix.<ServiceName>\`; файл меняется ежедневно и называется `yyyy-MM-dd.jsonl`. Порты сервисов заданы в `services.psd1`.

## Ограничения рабочего компьютера

- Для Arena необходим собранный Windows native-адаптер и настроенные серийные номера трёх камер.
- Для GPU-реконструкции необходим собранный native-модуль OpenCV CUDA/VPI и доступная CUDA-библиотека.
- Для сегментации необходим файл модели `models/rocks-v1.onnx`.
- MinIO доступен на `127.0.0.1:9000`, консоль — `127.0.0.1:9001`.
- NATS JetStream доступен на `127.0.0.1:4222`, мониторинг — `127.0.0.1:8222`.
