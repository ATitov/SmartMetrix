## Что изменено

<!-- Кратко опишите изменение и мотивацию. -->

## Связанный тикет

Closes #

## Проверка

- [ ] `dotnet restore SmartMetrix.sln --locked-mode`
- [ ] `dotnet format SmartMetrix.sln --no-restore --verify-no-changes`
- [ ] `dotnet build SmartMetrix.sln --no-restore --configuration Release`
- [ ] `dotnet test SmartMetrix.sln --no-build --configuration Release`
- [ ] Добавлены или обновлены тесты для изменённого поведения
- [ ] Документация обновлена, если изменился публичный контракт или процесс

## Риски

<!-- Укажите риски, миграции, совместимость и план отката. -->
