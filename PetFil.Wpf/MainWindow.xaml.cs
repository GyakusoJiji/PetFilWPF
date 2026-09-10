using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PetFil.Wpf
{
    public partial class MainWindow : Window
    {
        // Same defaults as printrun/petfil/gui.py GRAPH_STEPS / max_temp / WINDER_SPEED_MAX.
        private const int GraphSteps = 120;
        private const double MaxTemp = 300.0;

        private PetFilController? controller;
        private readonly Queue<double> currentTempHistory = new();
        private readonly Queue<double> targetTempHistory = new();
        private DispatcherTimer? graphTimer;
        private bool suppressSliderSync;
        private AppSettings settings = new();

        public MainWindow()
        {
            InitializeComponent();

            // Designer 中では実行時の初期化を行わない
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                controller = new PetFilController();
                controller.OnLog += Controller_OnLog;
                controller.OnConnectionChanged += Controller_OnConnectionChanged;
                controller.OnTempUpdated += Controller_OnTempUpdated;
                controller.OnWinderChanged += Controller_OnWinderChanged;
                Loaded += MainWindow_Loaded;
            }

            BaudCombo.ItemsSource = new[] { "2400", "9600", "19200", "38400", "57600", "115200", "250000" };
            BaudCombo.SelectedIndex = 5; // 115200

            TempSlider.Maximum = MaxTemp;
            TempTextBox.Text = "200";
            TempSlider.Value = 200;

            SpeedTextBox.Text = "300";
            SpeedSlider.Value = 300;

            // 前回終了時の条件から始める。デザイナ上ではファイルを触らない。
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                settings = AppSettings.Load();
                if (settings.Last is not null) ApplyPreset(settings.Last);
                UpdateSavedPresetLabel();
                Closed += MainWindow_Closed;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // 次回の起動はここで書き出した値から始まる。保存ボタンの値とは別枠なので、
            // 終了しても Saved は消えない。
            settings.Last = CurrentPreset();
            settings.Save();
        }

        /// <summary>いま欄に入っている運転条件。数値として読めない欄はスライダーの値で補う。</summary>
        private PetFilPreset CurrentPreset() => new()
        {
            Temp = double.TryParse(TempTextBox.Text, out var t) ? t : TempSlider.Value,
            Speed = double.TryParse(SpeedTextBox.Text, out var s) ? s : SpeedSlider.Value,
        };

        /// <summary>
        /// 条件を入力欄に流し込む。TextChanged 経由でスライダーも追従する。プリンタへは
        /// 送らない - 呼び出しただけで加熱が始まると危ないので、反映は「設定」に任せる。
        /// </summary>
        private void ApplyPreset(PetFilPreset preset)
        {
            TempTextBox.Text = preset.Temp.ToString("0", CultureInfo.InvariantCulture);
            SpeedTextBox.Text = preset.Speed.ToString("0", CultureInfo.InvariantCulture);
        }

        private void UpdateSavedPresetLabel()
        {
            SavedPresetLabel.Text = settings.Saved is { } preset ? $"保存値 {preset}" : "保存値はまだありません";
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshPorts();

            graphTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            graphTimer.Tick += (_, _) => SampleGraph();
            graphTimer.Start();
        }

        private void SampleGraph()
        {
            var current = controller?.CurrentTemp ?? 0.0;
            var target = controller?.TargetTemp ?? 0.0;

            currentTempHistory.Enqueue(current);
            targetTempHistory.Enqueue(target);
            while (currentTempHistory.Count > GraphSteps) currentTempHistory.Dequeue();
            while (targetTempHistory.Count > GraphSteps) targetTempHistory.Dequeue();

            DrawGraph();
        }

        private void DrawGraph()
        {
            var width = TempGraphCanvas.ActualWidth;
            var height = TempGraphCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            TempPolyline.Points = BuildPoints(currentTempHistory, width, height);
            TempTargetPolyline.Points = BuildPoints(targetTempHistory, width, height);
        }

        private PointCollection BuildPoints(Queue<double> history, double width, double height)
        {
            var points = new PointCollection();
            var values = history.ToArray();
            if (values.Length == 0) return points;

            var stepX = width / GraphSteps;
            var offset = GraphSteps - values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                var x = (offset + i) * stepX;
                var y = height - Math.Min(1.0, values[i] / MaxTemp) * height;
                points.Add(new Point(x, y));
            }
            return points;
        }

        private void RefreshPorts()
        {
            try
            {
                var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
                PortCombo.ItemsSource = ports;
                if (ports.Length > 0) PortCombo.SelectedIndex = 0;
                Log("ポートを検出しました: " + string.Join(", ", ports));
            }
            catch (Exception ex)
            {
                Log($"ポート検出エラー: {ex.Message}");
            }
        }

        private void Controller_OnConnectionChanged(object? sender, bool connected)
        {
            Dispatcher.Invoke(() =>
            {
                ConnectButton.Content = connected ? "切断" : "接続";
                Log(connected ? "接続されました。" : "切断しました。");
            });
        }

        private void Controller_OnTempUpdated(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var current = controller?.CurrentTemp;
                var target = controller?.TargetTemp;
                TempLabel.Text = $"{(current?.ToString("0.0") ?? "---")} / {(target?.ToString("0.0") ?? "---")} °C";
            });
        }

        private void Controller_OnWinderChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (controller is null) return;
                var status = controller switch
                {
                    { IsJogging: true, WinderDirection: > 0 } => "正転（高速）",
                    { IsJogging: true } => "逆転（高速）",
                    { IsWinding: true } => "運転中",
                    _ => "停止中",
                };
                WinderLabel.Text = $"{status}　積算 {controller.WinderTotalMm:0.0} mm";
            });
        }

        private void Controller_OnLog(object? sender, string line)
        {
            Log(line);
        }

        private void Log(string line)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (controller is null) return;
            if (controller.IsConnected)
            {
                controller.Disconnect();
                return;
            }
            var port = PortCombo.SelectedItem as string;
            var baud = BaudCombo.SelectedItem as string ?? "115200";
            if (string.IsNullOrWhiteSpace(port)) { Log("ポートが選択されていません。"); return; }
            if (controller.Connect(port, int.Parse(baud)))
            {
                Log($"接続中: {port} @ {baud}");
            }
        }

        private void RescanButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            controller?.Reset();
        }

        // 非常停止は左クリックが通常停止、右クリックが M112 による強制停止。
        // 咄嗟に押すものなので確認ダイアログは挟まない。
        private void EmergencyStopButton_Click(object sender, RoutedEventArgs e)
        {
            controller?.EmergencyStop();
        }

        private void EmergencyStopButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            controller?.Halt();
        }

        private void StartWinderButton_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(SpeedTextBox.Text, out var speed))
                controller?.StartWinder(speed);
            else
                controller?.StartWinder();
        }

        private void StopWinderButton_Click(object sender, RoutedEventArgs e)
        {
            controller?.StopWinder();
        }

        // 正転・逆転は押している間だけ動かす。ボタンは押下でマウスをキャプチャするので、
        // 離したときもポインタが外れたときも LostMouseCapture で確実に止まる。
        private void JogForwardButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StartJog(true);
        }

        private void JogReverseButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StartJog(false);
        }

        private void StartJog(bool forward)
        {
            if (controller is null) return;
            if (double.TryParse(SpeedTextBox.Text, out var speed)) controller.WinderSpeed = speed;
            controller.StartJog(forward);
        }

        private void JogButton_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            controller?.StopJog();
        }

        private void SetTempButton_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TempTextBox.Text, out var t)) controller?.SetTemperature(t);
        }

        private void HeaterOffButton_Click(object sender, RoutedEventArgs e)
        {
            controller?.HeaterOff();
        }

        private void TempTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (suppressSliderSync) return;
            if (double.TryParse(TempTextBox.Text, out var value))
            {
                suppressSliderSync = true;
                TempSlider.Value = Math.Clamp(value, TempSlider.Minimum, TempSlider.Maximum);
                suppressSliderSync = false;
            }
        }

        private void TempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderSync) return;
            suppressSliderSync = true;
            TempTextBox.Text = e.NewValue.ToString("0");
            suppressSliderSync = false;
        }

        private void SpeedTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (suppressSliderSync) return;
            if (double.TryParse(SpeedTextBox.Text, out var value))
            {
                suppressSliderSync = true;
                SpeedSlider.Value = Math.Clamp(value, SpeedSlider.Minimum, SpeedSlider.Maximum);
                suppressSliderSync = false;
                if (controller is { IsWinding: true }) controller.WinderSpeed = value;
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderSync) return;
            suppressSliderSync = true;
            SpeedTextBox.Text = e.NewValue.ToString("0");
            suppressSliderSync = false;
            if (controller is { IsWinding: true }) controller.WinderSpeed = e.NewValue;
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var preset = CurrentPreset();
            settings.Saved = preset;
            if (settings.Save())
                Log($"運転条件を保存しました: {preset}");
            else
                Log($"運転条件を保存できませんでした: {AppSettings.DefaultPath}");
            UpdateSavedPresetLabel();
        }

        private void LoadSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (settings.Saved is not { } preset)
            {
                Log("保存された運転条件がありません。");
                return;
            }
            ApplyPreset(preset);
            Log($"運転条件を呼び出しました: {preset}");
        }

        private void SendCommandButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = CommandBox.Text?.Trim();
            if (!string.IsNullOrEmpty(cmd))
            {
                controller?.SendCommand(cmd);
                CommandBox.Text = string.Empty;
            }
        }
    }
}
