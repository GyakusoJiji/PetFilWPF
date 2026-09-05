using System;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PetFil.Wpf
{
    // Minimal controller ported from Python: exposes connect/disconnect and simple send/receive.
    public class PetFilController
    {
        // Same defaults as printrun/petfil/controller.py: CHUNK_SEC / CHUNK_OVERLAP.
        public const double ChunkSec = 1.0;
        public const double ChunkOverlap = 1.2;

        // A single field of a temperature report: "T:205.3", "T0:205.3/210.0",
        // "B:0.0", "@:127", "B@:0", "W:?", the bare "/210.0" continuation of a
        // target, or a lone number. Ported from printrun/petfil/controller.py _TEMP_TOKEN.
        private static readonly Regex TempToken = new(
            @"^(?:(?:[TB]\d*|@\d*|B@|W):[-+]?\d*\.?\d*(?:/[-+]?\d*\.?\d*)?\?*|/[-+]?\d*\.?\d*|[-+]?\d*\.?\d+)$",
            RegexOptions.Compiled);

        private SerialPort? port;
        private Timer? winderTimer;

        public event EventHandler<string>? OnLog;
        public event EventHandler<bool>? OnConnectionChanged;
        public event EventHandler? OnTempUpdated;
        public event EventHandler? OnWinderChanged;

        public double? CurrentTemp { get; private set; }
        public double? TargetTemp { get; private set; }
        public bool SuppressTempLog { get; set; } = true;

        public bool IsWinding { get; private set; }
        public double WinderSpeed { get; set; } = 300.0;
        public double WinderTotalMm { get; private set; }

        public bool IsConnected => port != null && port.IsOpen;

        /// <summary>
        /// Length of a single winder movement chunk, in millimetres.
        /// Ported from printrun/petfil/controller.py winder_chunk_mm().
        /// </summary>
        public static double WinderChunkMm(double speedMmPerMin, double seconds = ChunkSec, double overlap = ChunkOverlap)
        {
            return Math.Max(0.0, speedMmPerMin) / 60.0 * seconds * overlap;
        }

        /// <summary>
        /// True for lines that carry nothing but a temperature report.
        /// Ported from printrun/petfil/controller.py is_temperature_report().
        /// </summary>
        public static bool IsTemperatureReport(string line)
        {
            var stripped = line.Trim();
            if (stripped.Length == 0 || !stripped.Contains("T:"))
                return false;
            if (stripped.StartsWith("ok"))
                stripped = stripped.Substring(2).Trim();
            var tokens = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 && tokens.All(t => TempToken.IsMatch(t));
        }

        /// <summary>
        /// Parses a temperature report line ("T:205.3 /210.0" style) and updates
        /// CurrentTemp/TargetTemp, mirroring PetFilController._update_temperatures.
        /// Returns true when the current temperature could be extracted.
        /// </summary>
        public bool TryUpdateTemperatures(string line)
        {
            foreach (var key in new[] { "T0", "T" })
            {
                var match = Regex.Match(line, key + @":([-+]?\d*\.?\d*)(?:\s*/([-+]?\d*\.?\d*))?");
                if (!match.Success) continue;

                double? current = string.IsNullOrEmpty(match.Groups[1].Value)
                    ? null
                    : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                double? target = string.IsNullOrEmpty(match.Groups[2].Value)
                    ? null
                    : double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                if (current is null) return false;
                CurrentTemp = current;
                if (target is not null) TargetTemp = target;
                OnTempUpdated?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }

        public bool Connect(string portName, int baudRate)
        {
            try
            {
                port = new SerialPort(portName, baudRate) { NewLine = "\n", Encoding = Encoding.ASCII };
                port.DataReceived += Port_DataReceived;
                port.Open();
                OnConnectionChanged?.Invoke(this, true);
                Log($"接続: {portName} @ {baudRate}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"接続エラー: {ex.Message}");
                port = null;
                OnConnectionChanged?.Invoke(this, false);
                return false;
            }
        }

        public void Disconnect()
        {
            StopWinder();
            try
            {
                if (port != null)
                {
                    port.DataReceived -= Port_DataReceived;
                    if (port.IsOpen) port.Close();
                    port.Dispose();
                    port = null;
                }
            }
            catch (Exception ex)
            {
                Log($"切断エラー: {ex.Message}");
            }
            OnConnectionChanged?.Invoke(this, false);
            Log("切断しました。");
        }

        private void Port_DataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (port == null) return;
                var line = port.ReadLine().TrimEnd('\r', '\n');
                var isTempReport = IsTemperatureReport(line);
                if (isTempReport)
                    TryUpdateTemperatures(line);
                if (isTempReport && SuppressTempLog)
                    return;
                Log(line);
            }
            catch (Exception ex)
            {
                Log($"受信エラー: {ex.Message}");
            }
        }

        public void SendCommand(string command)
        {
            try
            {
                if (port != null && port.IsOpen)
                {
                    port.WriteLine(command);
                    Log($"> {command}");
                }
                else
                {
                    Log("未接続のため送信できません。");
                }
            }
            catch (Exception ex)
            {
                Log($"送信エラー: {ex.Message}");
            }
        }

        public void Reset()
        {
            SendCommand("M999");
        }

        /// <summary>
        /// Starts winding the X axis at <see cref="WinderSpeed"/> mm/min, ported from
        /// PetFilController.start_winder in printrun/petfil/controller.py.
        /// </summary>
        public void StartWinder(double? speed = null)
        {
            if (speed is not null) WinderSpeed = speed.Value;
            if (IsWinding) return;
            SendCommand("M211 S0");
            SendCommand("G91");
            IsWinding = true;
            OnWinderChanged?.Invoke(this, EventArgs.Empty);
            winderTimer = new Timer(_ => WinderTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(ChunkSec));
        }

        /// <summary>
        /// Queues one movement chunk, mirroring PetFilController._winder_tick.
        /// </summary>
        private void WinderTick()
        {
            if (!IsWinding) return;
            var chunk = WinderChunkMm(WinderSpeed);
            SendCommand($"G1 X{chunk:0.000} F{WinderSpeed:0.0}");
            WinderTotalMm += chunk;
            OnWinderChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Stops winding and restores absolute positioning, ported from
        /// PetFilController.stop_winder.
        /// </summary>
        public void StopWinder()
        {
            if (!IsWinding) return;
            winderTimer?.Dispose();
            winderTimer = null;
            IsWinding = false;
            SendCommand("G90");
            SendCommand("M211 S1");
            OnWinderChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetTemperature(double temp)
        {
            TargetTemp = temp;
            OnTempUpdated?.Invoke(this, EventArgs.Empty);
            SendCommand($"M104 S{temp}");
        }

        public void HeaterOff()
        {
            SendCommand("M104 S0");
        }

        private void Log(string message)
        {
            OnLog?.Invoke(this, message);
        }
    }
}
