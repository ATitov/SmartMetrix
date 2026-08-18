# Операторский API

Панель доступна по `GET /operator`, API — под `/api/operator`. Внутренние адреса сервисов клиенту не возвращаются.

Доступ задаётся API-ключами через переменные окружения, например:

```text
OperatorApi__ApiKeys__operator-secret=operator
OperatorApi__ApiKeys__engineer-secret=engineer
OperatorApi__OrchestratorUrl=http://measurement-orchestrator:8080/
OperatorApi__Components__0__Name=Камеры
OperatorApi__Components__0__Kind=camera
OperatorApi__Components__0__ReadyUrl=http://camera-service:8080/ready
```

Ключ передаётся в `X-API-Key`. Роль `operator` может видеть состояние и вручную запускать измерение. Роль `engineer` дополнительно может отменять и повторять обработку; такие команды требуют `confirmed: true` и записываются в append-only JSONL-аудит.

Если URL зависимости отсутствует, её состояние — `NotConfigured`. Gateway не подставляет D50/D80, фракции или качество: отсутствующие результаты остаются пустыми.
