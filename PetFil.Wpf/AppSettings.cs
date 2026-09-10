using System;
using System.IO;
using System.Text.Json;

namespace PetFil.Wpf
{
    /// <summary>運転条件の一組。ノズル温度と巻き取り速度。</summary>
    public class PetFilPreset
    {
        public double Temp { get; set; }
        public double Speed { get; set; }

        public override string ToString() => $"{Temp:0} °C / {Speed:0} mm/min";
    }

    /// <summary>
    /// %APPDATA%\PetFil\settings.json に置く設定ファイル。
    ///
    /// <see cref="Last"/> は終了時に自動で書き出す「前回の最終設定」で、次回起動時の
    /// 初期値になる。<see cref="Saved"/> は保存ボタンで明示的に残す一組で、自動保存に
    /// 上書きされない。同じ枠にすると終了のたびに保存値が消えてしまうため分けてある。
    /// </summary>
    public class AppSettings
    {
        public PetFilPreset? Last { get; set; }
        public PetFilPreset? Saved { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>設定ファイルの既定の置き場所。</summary>
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PetFil", "settings.json");

        /// <summary>
        /// 設定を読む。ファイルが無いときも壊れているときも、起動できなくなるほうが
        /// 困るので既定値を返す。
        /// </summary>
        public static AppSettings Load(string? path = null)
        {
            path ??= DefaultPath;
            try
            {
                if (!File.Exists(path)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }

        /// <summary>設定を書き出す。書けなかったときは false を返して呼び出し側に知らせる。</summary>
        public bool Save(string? path = null)
        {
            path ??= DefaultPath;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
