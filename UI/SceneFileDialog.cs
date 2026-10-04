using System;
using System.IO;
using System.Windows.Forms;

namespace Phyxel.UI;

internal static class SceneFileDialog
{
    internal static string? Select(string currentPath, bool save, IntPtr ownerHandle,
        Func<FileDialog, IWin32Window, DialogResult>? show = null)
    {
        using FileDialog dialog = save ? new SaveFileDialog { OverwritePrompt = true }
            : new OpenFileDialog { Multiselect = false, CheckFileExists = true };
        dialog.Title = save ? "Сохранить сцену Phyxel" : "Загрузить сцену Phyxel";
        dialog.Filter = "Сцены Phyxel (*.json)|*.json";
        dialog.DefaultExt = "json";
        dialog.AddExtension = true;
        dialog.RestoreDirectory = true;
        dialog.CheckPathExists = true;
        string path = Path.GetFullPath(currentPath);
        string? directory = Path.GetDirectoryName(path);
        if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
        dialog.FileName = Path.GetFileName(path);
        var owner = new Owner(ownerHandle);
        DialogResult result = (show ?? ((d, w) => d.ShowDialog(w)))(dialog, owner);
        if (result != DialogResult.OK) return null;
        string selected = Path.GetFullPath(dialog.FileName);
        // A .world file must never become the JSON destination: the serializer
        // uses the same stem for the companion binary file.
        if (!string.Equals(Path.GetExtension(selected), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите файл сцены с расширением .json.");
        return selected;
    }

    private sealed class Owner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle => handle;
    }
}
