using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PetFil.Wpf
{
    /// <summary>
    /// Serial controller ported from printrun/petfil/controller.py. The Python
    /// version delegates the transport to printrun.printcore, so the handshake,
    /// the "ok" flow control and the temperature poll that live in printcore are
    /// reproduced here.
    /// </summary>
    public class PetFilController
    {
        // Same defaults as printrun/petfil/controller.py: CHUNK_SEC / CHUNK_OVERLAP /
        // MAX_QUEUE_DEPTH / TEMP_POLL_SEC.
        public const double ChunkSec = 1.0;
        public const double ChunkOverlap = 1.2;
        public const int MaxQueueDepth = 2;
        public const double TempPollSec = 2.0;

        // Marlin resets when DTR is asserted on open; nothing sent while the
        // bootloader runs is executed, so wait it out before the handshake.
        private const int BootDelayMs = 2000;
        private const int HandshakeIntervalMs = 1000;
        private const int HandshakeAttempts = 20;
        // Upper bound on how long a single command may block the send loop when
        // its "ok" never arrives.
        private const int AckTimeoutMs = 10000;

        // A single field of a temperature report: "T:205.3", "T0:205.3/210.0",
        // "B:0.0", "@:127", "B@:0", "W:?", the bare "/210.0" continuation of a
        // target, or a lone number. Ported from printrun/petfil/controller.py _TEMP_TOKEN.
        private static readonly Regex TempToken = new(
            @"^(?:(?:[TB]\d*|@\d*|B@|W):[-+]?\d*\.?\d*(?:/[-+]?\d*\.?\d*)?\?*|/[-+]?\d*\.?\d*|[-+]?\d*\.?\d+)$",
            RegexOptions.Compiled);

        private SerialPort? port;
        private Timer? winderTimer;
        private Timer? tempTimer;
        private Thread? readThread;
        private Thread? sendThread;
        private BlockingCollection<string>? sendQueue;
        private CancellationTokenSource? cts;
        private readonly ManualResetEventSlim okEvent = new(false);
        private volatile bool sawAnyLine;
        private long lastAutoReportTicks;

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
        /// True once the printer has answered the handshake. Mirrors
        /// PetFilController.is_online; commands are refused before that because
        /// anything sent while the firmware is booting is silently discarded.
        /// </summary>
        public bool IsOnline { get; private set; }

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
                port = new SerialPort(portName, baudRate)
                {
                    NewLine = "\n",
                    Encoding = Encoding.ASCII,
                    // printrun/device.py opens the port with pyserial timeout=0.25.
                    ReadTimeout = 250,
                    WriteTimeout = 5000,
                    // pyserial raises DTR/RTS when it opens a port; SerialPort leaves
                    // them low, which keeps USB-CDC boards from accepting any data.
                    DtrEnable = true,
                    RtsEnable = true,
                };
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                cts = new CancellationTokenSource();
                sendQueue = new BlockingCollection<string>();
                sawAnyLine = false;
                IsOnline = false;
                okEvent.Set();

                readThread = new Thread(ReadLoop) { IsBackground = true, Name = "petfil read" };
                readThread.Start();
                sendThread = new Thread(SendLoop) { IsBackground = true, Name = "petfil send" };
                sendThread.Start();

                OnConnectionChanged?.Invoke(this, true);
                Log($"接続: {portName} @ {baudRate}");

                new Thread(Handshake) { IsBackground = true, Name = "petfil handshake" }.Start();
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

        /// <summary>
        /// Waits for the firmware to finish booting and polls M105 until it
        /// answers, then enables auto temperature reports. This is what printcore
        /// does before PetFilController._on_online fires.
        /// </summary>
        private void Handshake()
        {
            var token = cts?.Token ?? CancellationToken.None;
            try
            {
                Log("プリンタの起動を待っています...");
                if (token.WaitHandle.WaitOne(BootDelayMs)) return;

                for (var i = 0; i < HandshakeAttempts && !token.IsCancellationRequested; i++)
                {
                    if (sawAnyLine) break;
                    if (!WriteDirect("M105")) return;
                    if (token.WaitHandle.WaitOne(HandshakeIntervalMs)) return;
                }

                if (!sawAnyLine)
                {
                    Log("プリンタが応答しません。ボーレートと接続を確認してください。");
                    return;
                }

                IsOnline = true;
                Log("プリンタがオンラインになりました。");
                // PetFilController._on_online: turn on Marlin auto temperature reports.
                SendCommand("M155 S1");

                tempTimer = new Timer(_ => TempTick(), null,
                    TimeSpan.FromSeconds(TempPollSec), TimeSpan.FromSeconds(TempPollSec));
            }
            catch (Exception ex)
            {
                Log($"接続処理エラー: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            StopWinder();
            IsOnline = false;
            try
            {
                cts?.Cancel();
                tempTimer?.Dispose();
                tempTimer = null;
                sendQueue?.CompleteAdding();
                okEvent.Set();

                readThread?.Join(1000);
                sendThread?.Join(1000);
                readThread = null;
                sendThread = null;

                if (port != null)
                {
                    if (port.IsOpen) port.Close();
                    port.Dispose();
                    port = null;
                }

                sendQueue?.Dispose();
                sendQueue = null;
                cts?.Dispose();
                cts = null;
            }
            catch (Exception ex)
            {
                Log($"切断エラー: {ex.Message}");
            }
            CurrentTemp = null;
            OnTempUpdated?.Invoke(this, EventArgs.Empty);
            OnConnectionChanged?.Invoke(this, false);
            Log("切断しました。");
        }

        /// <summary>
        /// Reads one line at a time, mirroring printcore's listener: "ok" clears
        /// the send gate, temperature reports refresh the readout.
        /// </summary>
        private void ReadLoop()
        {
            var token = cts?.Token ?? CancellationToken.None;
            while (!token.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = port!.ReadLine();
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) Log($"受信エラー: {ex.Message}");
                    return;
                }

                line = line.Trim('\r', '\n', ' ');
                if (line.Length == 0) continue;
                sawAnyLine = true;

                // "ok" acknowledges the previous command and releases the send loop.
                if (line.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
                    okEvent.Set();

                var isTempReport = IsTemperatureReport(line);
                if (isTempReport)
                {
                    Interlocked.Exchange(ref lastAutoReportTicks, Environment.TickCount64);
                    TryUpdateTemperatures(line);
                    if (SuppressTempLog) continue;
                }
                Log(line);
            }
        }

        /// <summary>
        /// Sends one queued command at a time and waits for its "ok" before the
        /// next, so the firmware's small receive buffer never overflows.
        /// </summary>
        private void SendLoop()
        {
            var token = cts?.Token ?? CancellationToken.None;
            try
            {
                foreach (var command in sendQueue!.GetConsumingEnumerable(token))
                {
                    okEvent.Reset();
                    if (!WriteDirect(command)) return;
                    if (!okEvent.Wait(AckTimeoutMs, token))
                        Log($"応答待ちタイムアウト: {command}");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log($"送信エラー: {ex.Message}");
            }
        }

        /// <summary>Writes a command straight to the port, bypassing the "ok" gate.</summary>
        private bool WriteDirect(string command)
        {
            try
            {
                if (port == null || !port.IsOpen) return false;
                port.WriteLine(command);
                Log($"> {command}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"送信エラー: {ex.Message}");
                return false;
            }
        }

        private void TempTick()
        {
            if (!IsOnline) return;
            // M155 S1 normally keeps the readout fresh; only poll when it went quiet,
            // as PetFilController._temp_loop does.
            var since = Environment.TickCount64 - Interlocked.Read(ref lastAutoReportTicks);
            if (since < TempPollSec * 1000) return;
            Enqueue("M105");
        }

        private void Enqueue(string command)
        {
            try
            {
                if (sendQueue is { IsAddingCompleted: false }) sendQueue.Add(command);
            }
            catch (InvalidOperationException)
            {
                // Queue was closed while disconnecting.
            }
        }

        public void SendCommand(string command)
        {
            command = command.Trim();
            if (command.Length == 0) return;
            if (!IsConnected)
            {
                Log("未接続のため送信できません。");
                return;
            }
            if (!IsOnline)
            {
                Log("プリンタがまだオンラインではありません。");
                return;
            }
            Enqueue(command);
        }

        /// <summary>
        /// Pulses DTR to reset the board, as printrun/device.py _reset_serial does,
        /// then repeats the online handshake.
        /// </summary>
        public void Reset()
        {
            if (port == null || !port.IsOpen)
            {
                Log("未接続のため送信できません。");
                return;
            }
            try
            {
                StopWinder();
                IsOnline = false;
                sawAnyLine = false;
                tempTimer?.Dispose();
                tempTimer = null;
                port.DtrEnable = true;
                Thread.Sleep(200);
                port.DtrEnable = false;
                Thread.Sleep(200);
                port.DtrEnable = true;
                Log("リセットを送信しました。");
                new Thread(Handshake) { IsBackground = true, Name = "petfil handshake" }.Start();
            }
            catch (Exception ex)
            {
                Log($"リセットエラー: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts winding the X axis at <see cref="WinderSpeed"/> mm/min, ported from
        /// PetFilController.start_winder in printrun/petfil/controller.py.
        /// </summary>
        public void StartWinder(double? speed = null)
        {
            if (speed is not null) WinderSpeed = speed.Value;
            if (IsWinding) return;
            if (!IsOnline)
            {
                Log("プリンタがまだオンラインではありません。");
                return;
            }
            SendCommand("M211 S0");
            SendCommand("G91");
            IsWinding = true;
            OnWinderChanged?.Invoke(this, EventArgs.Empty);
            winderTimer = new Timer(_ => WinderTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(ChunkSec));
        }

        /// <summary>
        /// Queues one movement chunk, mirroring PetFilController._winder_tick.
        /// Skipped while the queue is still draining so the moves stay in step with
        /// the firmware instead of piling up.
        /// </summary>
        private void WinderTick()
        {
            if (!IsWinding || !IsOnline) return;
            if ((sendQueue?.Count ?? 0) >= MaxQueueDepth) return;
            var chunk = WinderChunkMm(WinderSpeed);
            // G92 X0 keeps the relative moves anchored, as in _winder_tick.
            Enqueue("G92 X0");
            Enqueue(string.Format(CultureInfo.InvariantCulture, "G1 X{0:0.000} F{1:0.0}", chunk, WinderSpeed));
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
            SendCommand(string.Format(CultureInfo.InvariantCulture, "M104 S{0:0}", temp));
        }

        public void HeaterOff()
        {
            SendCommand("M104 S0");
            TargetTemp = 0.0;
            OnTempUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void Log(string message)
        {
            OnLog?.Invoke(this, message);
        }
    }
}
