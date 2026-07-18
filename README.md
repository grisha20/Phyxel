# Phyxel

Phyxel — ранний прототип двумерной песочницы, в которой материалы и их взаимодействия рассчитываются на GPU. Цель проекта — универсальное клеточное ядро: новый материал описывается данными, а общие механики добавляются в движок независимо от конкретных `MaterialIndex`.

Проект написан на C# под .NET 8 for Windows, использует KNI/MonoGame-подобный игровой слой, DirectX 11 и HLSL compute shaders. Клеточная физика, композиция кадра и теплопередача выполняются на GPU.

Сейчас проект находится на ранней стадии разработки. Уже работают базовая физика granular/liquid/gas/solid, JSON-материалы, сохранения, универсальная температура с ручным инструментом и первая data-driven фазовая цепочка `water ↔ ice ↔ steam`. Реакции, огонь, контент-паки и сетевой hub ещё не реализованы.

## Быстрый запуск

Требуются Windows, .NET 8 SDK и видеокарта с DirectX 11.

```powershell
dotnet build Phyxel.sln -c Debug --nologo
dotnet run --project Phyxel.csproj -c Debug
```

Также Debug-сборку можно запустить командой `build.bat debug`, а готовую игру — из `bin/Debug/net8.0-windows/Phyxel.exe`.

## Документация

- [Текущее состояние и roadmap](docs/PROJECT_STATUS.md)
- [Архитектура](docs/ARCHITECTURE.md)
- [Сборка, проверки и правила разработки](docs/DEVELOPMENT.md)

Последний проверенный коммит документации: `Add water ice steam phase chain` (SHA указывается в handoff после создания коммита).
