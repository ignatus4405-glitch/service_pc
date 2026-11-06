# Инструкции для агентов и сопровождающих

Прочитайте этот файл перед изменением проекта. Он описывает обязательные продуктовые ограничения и порядок продолжения работы.

## Продукт и целевые платформы

- Продукт: PC Care, Windows desktop app, русский WinForms UI.
- Целевой runtime — **.NET Framework 4.5** и C# 5. Минимальная система — Windows 7 SP1; также поддерживаются Windows 8, 8.1, 10 и 11. Не повышайте TargetFramework без проверки этих платформ.
- Репозиторий изначально не содержал приложения. Исходники находятся в `src/PcCare`; помощник обновления — `src/PcCare.Updater`; тесты — `src/PcCare.Tests`.
- Функциональные ориентиры (не заимствованные исходники/брендинг): типовой inventory CPU-Z/HWiNFO, просмотр SMART CrystalDiskInfo, осторожная очистка BleachBit, штатные Optimize Drives/Disk Cleanup Windows. Код и интерфейс PC Care — самостоятельные.

## Критичные правила безопасности

- Диагностика остаётся read-only и выполняется локально через WMI. Не добавляйте сбор/отправку системной информации.
- SMART «не поддерживается/нет данных» не равен «исправен». WMI SMART-флаг — только предварительный сигнал, не обещание предсказать отказ.
- Очистка ограничена TEMP текущего пользователя и файлами старше 24 часов; reparse points, недавние/занятые файлы пропускаются, TEMP-корень диска и системные/пользовательские папки с данными блокируются. Не добавляйте скрытое удаление browser data, документов, корзины, Prefetch, системных файлов или реестра.
- Не запускайте тяжёлые тесты, стресс-тесты, разгон, дефрагментацию/оптимизацию или изменения службы автоматически. Для обслуживания открывайте штатный инструмент Windows и оставляйте решение пользователю.
- Приложение запускается `asInvoker`; UAC допускается только при подтверждённой пользователем установке обновления в защищённую папку.
- Автообновление скачивает только HTTPS asset `*-windows.zip` из релиза GitHub, уведомляет пользователя и обновляет только после подтверждения. Не внедряйте скрытые обновления.

## Где что находится

- `src/PcCare/Models` — модели snapshot, накопителей, рекомендаций и release metadata.
- `src/PcCare/Diagnostics/DiagnosticService.cs` — WMI-снимок CPU/RAM/OS/disks/SMART.
- `src/PcCare/Diagnostics/RecommendationEngine.cs` — детерминированные правила советов; тесты здесь важны.
- `src/PcCare/Diagnostics/CleanupService.cs` — ограниченная безопасная очистка.
- `src/PcCare/Diagnostics/WindowsMaintenance.cs` — только запуск встроенных Windows UI.
- `src/PcCare/Updates/UpdateService.cs` — GitHub API, ZIP download и запуск помощника.
- `src/PcCare/MainForm.cs` — UI и пользовательские подтверждения.
- `src/PcCare.Updater/Program.cs` — ожидание завершения основного процесса, замена EXE и перезапуск.
- `.github/workflows/ci.yml` — Windows build/test на push/PR; `.github/workflows/release.yml` — release при push `v*.*.*`.
- `scripts/set-version.ps1` — перевод semver-тега в AssemblyVersion/FileVersion/InformationalVersion.
- `docs/` — дополнительные пояснения; `CHANGELOG.md` фиксирует заметные изменения.

## Сборка и проверки

Исполняйте на Windows с MSBuild и NuGet:

```powershell
nuget install .\build\packages.config -OutputDirectory packages -NonInteractive
$refs = Join-Path $PWD 'packages\Microsoft.NETFramework.ReferenceAssemblies.net45.1.0.3\build\Microsoft.NETFramework.ReferenceAssemblies.net45.targets'
msbuild PcCare.sln /m /p:Configuration=Release "/p:CustomAfterMicrosoftCommonTargets=$refs" /verbosity:minimal
.\src\PcCare.Tests\bin\Release\PcCare.Tests.exe
```

На Linux/macOS нет поддерживаемой локальной WinForms-сборки; используйте GitHub Actions (`windows-latest`). Не утверждайте, что Windows-сборка проверена локально, если она не запускалась.

Любое новое правило рекомендаций добавляйте с тестом в `src/PcCare.Tests/Program.cs`. Перед завершением:

1. Обновите `CHANGELOG.md` и соответствующие документы в `docs/`/`README.md`.
2. Проверьте `git diff --check`, структуру проектов/solution и C#-совместимость с LangVersion 5/.NET 4.5.
3. Запустите CI через push на рабочую ветку или GitHub Actions и проверьте результат; не скрывайте неуспешную сборку.
4. Не добавляйте `bin`, `obj`, NuGet packages, release ZIP и другие сборочные артефакты в Git.

## Версии и обновления

- GitHub repository, проверяемый клиентом, зафиксирован в `UpdateService.cs` как `ignatus4405-glitch/service_pc`. При переезде репозитория обновите там URL и документацию.
- Стабильный релиз имеет ровно тег `vX.Y.Z`, например `v1.2.0`. Release workflow должен создать файл `PcCare-vX.Y.Z-windows.zip` с `PcCare.exe` и `PcCare.Updater.exe` в корне ZIP.
- `UpdateService` ожидает asset с окончанием `-windows.zip`. Не меняйте имя/структуру пакета без синхронного обновления клиента, workflow и теста/документации.
- Клиент сравнивает три части версии (major/minor/patch); релизы должны быть семантическими, без суффиксов `-beta`.
- Action versions и NuGet manifests управляются Dependabot (`.github/dependabot.yml`).

## Правило документирования изменений

Каждая заметная фича, безопасность/поведение, целевая платформа, формат релиза или сборочный процесс должна сопровождаться:
- записью в `CHANGELOG.md`;
- обновлением `README.md` и/или соответствующего документа в `docs/`;
- тестом для новых детерминированных правил.

Оставляйте инструкции пригодными для следующего агента: фиксируйте несовместимости/провал CI, не полагайтесь на знания из чата и не переписывайте этот файл без причины.
