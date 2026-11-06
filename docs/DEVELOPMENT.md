# Разработка и проверка

## Требования к окружению

- Windows 7 SP1–Windows 11 для запуска; .NET Framework 4.5 runtime или новее.
- Для сборки: Visual Studio Build Tools/MSBuild, NuGet CLI и доступ к NuGet.org для reference assemblies.
- Все проекты в `PcCare.sln` используют .NET Framework 4.5 и C# 5. Не используйте API/синтаксис только из новых версий .NET без проверки на целевых Windows.

## Сборка

```powershell
nuget install .\build\packages.config -OutputDirectory packages -NonInteractive
./scripts/build.ps1 -Configuration Release
.\src\PcCare.Tests\bin\Release\PcCare.Tests.exe
```

Эквивалентный Windows job находится в `.github/workflows/ci.yml`. Он запускается на push в `main` и `arena/**`, на pull request к `main` и вручную через `workflow_dispatch`; в job собираются приложение, помощник обновления и тестовый console runner.

## Проверка перед изменением

- `DiagnosticService` должен допускать частичный сбор: отсутствующий WMI provider — заметка/неизвестное значение, а не ложный «здоров».
- Новый порог в `RecommendationEngine` покрыть отдельным тестом в `src/PcCare.Tests/Program.cs`.
- Изменения CPU/RAM тестов покрыть детерминированными проверками checksum/лимитов памяти; CI не должен запускать реальную нагрузку на CPU/RAM. Вручную проверять отмену, отображение прогресса и реакцию на нехватку свободной памяти на Windows.
- Проверить запуск на реальных устройствах Windows (особенно SMART NVMe/USB, диски с низким свободным местом); CI не подключает SMART-железо.
- Для очистки проверять недавние файлы, locked-файлы, read-only, symlink/junction, ошибочный TEMP (корень диска/системные/пользовательские папки) и отсутствие прав. Не ослаблять возрастной предел без документированного обоснования.
- Для update-пакета проверять имена файлов в корне ZIP и что `PcCare.Updater.exe` способен заменить приложение после выхода процесса.
- Изменения UI должны сохранять явные предупреждения и подтверждения пользователя.

## Сборка локально на Linux/macOS

WinForms/.NET Framework 4.5 не является поддерживаемой локальной сборкой в этом sandbox. Используйте GitHub Actions `windows-latest`; не заменяйте проект на более новый runtime только ради локального компилятора, так как это может потерять Windows 7/8.
