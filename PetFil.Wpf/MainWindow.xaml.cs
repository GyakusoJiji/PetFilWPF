using System;
using System.Collections.Generic;
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
                var status = controller.IsWinding ? "運転中" : "停止中";
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
