# Phyxel

Phyxel — ранний прототип двумерной GPU-песочницы. Материалы описываются JSON-файлами, получают непостоянный `RuntimeIndex` при запуске, а клеточная физика, температура, фазовые переходы, горение и рендеринг выполняются compute-шейдерами DirectX 11.

Сейчас реализованы:

- общая физика granular, liquid, gas, movable solid и fixture;
- внешние материалы без пересборки игры;
- температура, теплопередача и ручной температурный инструмент;
- data-driven переходы `water ↔ ice ↔ steam`;
- дерево, древесный и каменный уголь, огонь, дым и CO₂;
- независимое от FPS охлаждение пара и перераспределение газов по плотности;
- сохранения v6 и загрузка legacy-сцен v3/v4/v5.

Проект написан на C#/.NET 8 for Windows и использует KNI, DirectX 11 и HLSL. Это рабочее ядро, а не законченная игра: полноценная система произвольных реакций, взрывы, пакеты контента, магазин и сетевой hub пока не реализованы.

## Быстрый запуск

Нужны Windows, .NET 8 SDK и видеокарта с DirectX 11.

```powershell
dotnet build Phyxel.sln -c Debug --nologo
dotnet run --project Phyxel.csproj -c Debug
```

Готовая Debug-сборка запускается из `bin/Debug/net8.0-windows/Phyxel.exe`.

## Документация

- [Текущее состояние](docs/PROJECT_STATUS.md)
- [Архитектура](docs/ARCHITECTURE.md)
- [Газовая симуляция](docs/GAS_SIMULATION.md)
- [Фазовые переходы](docs/PHASE_TRANSITIONS_DESIGN.md)
- [Горение](docs/COMBUSTION_DESIGN.md)
- [Сборка и разработка](docs/DEVELOPMENT.md)
