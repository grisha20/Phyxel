using System.Collections.Generic;
using Phyxel.Materials;

namespace Phyxel.UI;

public static class UiLocalizationProvider
{
    private static readonly IReadOnlyDictionary<MaterialId, string> MaterialNames =
        new Dictionary<MaterialId, string>
        {
            [MaterialId.Sand] = "Песок",
            [MaterialId.Water] = "Вода",
            [MaterialId.Metal] = "Металл",
            [MaterialId.Concrete] = "Бетон",
            [MaterialId.Eraser] = "Ластик",
            [MaterialId.Gas] = "Газ"
        };

    public static string Material(MaterialId materialId) => MaterialNames[materialId];
    public static string BrushSize => "Размер кисти";
    public static string SpawnDensity => "Плотность спавна";
    public static string Pause => "Пауза симуляции";
    public static string Continue => "Продолжить симуляцию";
    public static string Clear => "Очистить всё";
    public static string ConfirmClear => "Подтвердить очистку";
    public static string Save => "Сохранить сцену";
    public static string Load => "Загрузить сцену";
    public static string Stress => "Показать напряжение";
    public static string Materials => "МАТЕРИАЛЫ";
}
