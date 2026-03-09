using anticheatgui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace anticheatgui
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly Label discordLabel;
        private readonly TextBox discordTextBox;

        private readonly Label robloxLabel;
        private readonly TextBox robloxTextBox;

        private readonly Button startButton;
        private readonly Button stopButton;

        private readonly Label statusTitleLabel;
        private readonly Label statusValueLabel;

        private readonly AntiCheatMonitor monitor;

        public MainForm()
        {
            Text = "Roblox Anti-Cheat";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 220);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            discordLabel = new Label
            {
                Text = "Discord Username",
                Left = 20,
                Top = 20,
                Width = 150
            };

            discordTextBox = new TextBox
            {
                Left = 20,
                Top = 42,
                Width = 370
            };

            robloxLabel = new Label
            {
                Text = "Roblox Username",
                Left = 20,
                Top = 78,
                Width = 150
            };

            robloxTextBox = new TextBox
            {
                Left = 20,
                Top = 100,
                Width = 370
            };

            startButton = new Button
            {
                Text = "Start",
                Left = 20,
                Top = 140,
                Width = 120,
                Height = 32
            };

            stopButton = new Button
            {
                Text = "Stop",
                Left = 150,
                Top = 140,
                Width = 120,
                Height = 32,
                Enabled = false
            };

            statusTitleLabel = new Label
            {
                Text = "Status:",
                Left = 20,
                Top = 185,
                Width = 50
            };

            statusValueLabel = new Label
            {
                Text = "Idle",
                Left = 75,
                Top = 185,
                Width = 300
            };

            Controls.Add(discordLabel);
            Controls.Add(discordTextBox);
            Controls.Add(robloxLabel);
            Controls.Add(robloxTextBox);
            Controls.Add(startButton);
            Controls.Add(stopButton);
            Controls.Add(statusTitleLabel);
            Controls.Add(statusValueLabel);

            monitor = new AntiCheatMonitor(UpdateStatusSafe);

            startButton.Click += StartButton_Click;
            stopButton.Click += StopButton_Click;
            FormClosing += MainForm_FormClosing;
        }

        private async void StartButton_Click(object? sender, EventArgs e)
        {
            string discordUsername = Clean(discordTextBox.Text);
            string robloxUsername = Clean(robloxTextBox.Text);

            if (discordUsername == "Unknown")
            {
                MessageBox.Show("Please enter a Discord username.", "Missing info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (robloxUsername == "Unknown")
            {
                MessageBox.Show("Please enter a Roblox username.", "Missing info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            startButton.Enabled = false;
            stopButton.Enabled = true;
            discordTextBox.Enabled = false;
            robloxTextBox.Enabled = false;

            await monitor.StartAsync(discordUsername, robloxUsername);
        }

        private async void StopButton_Click(object? sender, EventArgs e)
        {
            stopButton.Enabled = false;
            UpdateStatusSafe("Stopping...");
            await monitor.StopAsync();
            startButton.Enabled = true;
            discordTextBox.Enabled = true;
            robloxTextBox.Enabled = true;
            UpdateStatusSafe("Stopped");
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (monitor.IsRunning)
            {
                monitor.StopAsync().GetAwaiter().GetResult();
            }
        }

        private void UpdateStatusSafe(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateStatusSafe), text);
                return;
            }

            statusValueLabel.Text = text;
        }

        private static string Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "Unknown";

            return s.Replace("@", "").Replace("\r", "").Replace("\n", "").Trim();
        }
    }

    public class AntiCheatMonitor
    {
        private readonly Action<string> setStatus;
        private readonly HttpClient http = new HttpClient();

        private const string WebhookUrl = "https://discord.com/api/webhooks/1480432933100589129/03ZqNhe6kzmyp8xV1G2lVED6ZoQqxn8HVFaMlenXk_fm5xJvZBlaJ2qiPLun4HrlJDgN";

        private string discordUsername = "Unknown";
        private string robloxUsername = "Unknown";
        private string sessionId = "";
        private DateTime startTime;

        private int robloxPid = -1;
        private CancellationTokenSource? cts;
        private Task? monitorTask;
        private bool finalWebhookSent = false;

        private readonly HashSet<int> knownProcesses = new HashSet<int>();
        private readonly List<string> logs = new List<string>();
        private readonly Dictionary<int, int> processScores = new Dictionary<int, int>();
        private readonly Dictionary<int, string> processReasons = new Dictionary<int, string>();
        private readonly Dictionary<string, DateTime> overlayCooldown = new Dictionary<string, DateTime>();

        private readonly TimeSpan overlayDelay = TimeSpan.FromMinutes(3);

        public bool IsRunning { get; private set; }

        private readonly HashSet<string> suspiciousProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cheatengine",
            "processhacker",
            "x64dbg",
            "x32dbg",
            "ida",
            "ollydbg",
            "dnspy",
            "ghidra",
            "megadumper",
            "reclass",
            "reclass.net",
            "httpdebuggerui",
            "fiddler",
            "wireshark",
            "loader",
            "overlay"
        };

        private readonly string[] suspiciousPathKeywords =
        {
            @"\AppData\Local\Temp\",
            @"\AppData\Roaming\",
            @"\Temp\"
        };

        private readonly string[] suspiciousWindowKeywords =
        {
            "overlay",
            "esp",
            "cheat",
            "injector",
            "executor",
            "debug",
            "loader"
        };

        public AntiCheatMonitor(Action<string> setStatus)
        {
            this.setStatus = setStatus;
        }

        public Task StartAsync(string discordUsername, string robloxUsername)
        {
            if (IsRunning)
                return Task.CompletedTask;

            this.discordUsername = discordUsername;
            this.robloxUsername = robloxUsername;
            this.sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            this.startTime = DateTime.Now;

            finalWebhookSent = false;
            logs.Clear();
            knownProcesses.Clear();
            processScores.Clear();
            processReasons.Clear();
            overlayCooldown.Clear();

            cts = new CancellationTokenSource();
            IsRunning = true;

            monitorTask = Task.Run(() => MonitorLoopAsync(cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (!IsRunning)
                return;

            try
            {
                cts?.Cancel();

                if (monitorTask != null)
                    await monitorTask;
            }
            catch
            {
            }

            await SendFinalWebhook();
            IsRunning = false;
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            try
            {
                Log("Anti-Cheat Started");
                setStatus("Waiting for Roblox...");

                robloxPid = await WaitForRobloxAsync(token);
                BuildProcessList();

                Log($"Roblox detected. PID: {robloxPid}");
                setStatus("Monitoring");

                while (!token.IsCancellationRequested)
                {
                    if (!IsRobloxRunning())
                    {
                        Log("Roblox closed.");
                        setStatus("Roblox closed");
                        await SendFinalWebhook();
                        IsRunning = false;
                        return;
                    }

                    ScanProcesses();
                    ScanOverlays();

                    await Task.Delay(2000, token);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                setStatus("Error");
            }
            finally
            {
                IsRunning = false;
            }
        }

        private async Task<int> WaitForRobloxAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                Process? p = null;

                try
                {
                    p = Process.GetProcessesByName("RobloxPlayerBeta").FirstOrDefault();
                    if (p != null)
                        return p.Id;
                }
                finally
                {
                    p?.Dispose();
                }

                await Task.Delay(2000, token);
            }
        }

        private bool IsRobloxRunning()
        {
            try
            {
                using Process p = Process.GetProcessById(robloxPid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void BuildProcessList()
        {
            knownProcesses.Clear();

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    knownProcesses.Add(p.Id);
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        private void ScanProcesses()
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (knownProcesses.Contains(p.Id))
                        continue;

                    knownProcesses.Add(p.Id);

                    string processName = p.ProcessName;
                    string path = TryGetProcessPath(p.Id);
                    string signer = TryGetFileSigner(path);

                    int score = 0;
                    List<string> reasons = new List<string>();

                    if (suspiciousProcesses.Contains(processName))
                    {
                        score += processName.Equals("loader", StringComparison.OrdinalIgnoreCase) ||
                                 processName.Equals("overlay", StringComparison.OrdinalIgnoreCase)
                            ? 80
                            : 60;

                        reasons.Add("suspicious name");
                    }

                    if (IsSuspiciousPath(path))
                    {
                        score += 20;
                        reasons.Add("suspicious path");
                    }

                    if (IsUnsignedOrUnknownSigner(signer))
                    {
                        score += 20;
                        reasons.Add("unsigned/unknown signer");
                    }

                    processScores[p.Id] = score;
                    processReasons[p.Id] = reasons.Count > 0 ? string.Join(", ", reasons) : "none";

                    if (score >= 100)
                    {
                        Log($"[HIGH RISK PROCESS] {processName} | PID {p.Id} | Score {score} | Path: {path} | Signer: {signer} | Reasons: {processReasons[p.Id]}");
                    }
                    else if (score >= 70)
                    {
                        Log($"[SUSPICIOUS PROCESS] {processName} | PID {p.Id} | Score {score} | Path: {path} | Signer: {signer} | Reasons: {processReasons[p.Id]}");
                    }
                    else
                    {
                        Log($"[PROCESS] {processName} | PID {p.Id} | Score {score}");
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        private void ScanOverlays()
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd))
                        return true;

                    int length = GetWindowTextLength(hWnd);
                    if (length == 0)
                        return true;

                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);

                    string title = sb.ToString();
                    if (string.IsNullOrWhiteSpace(title))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

                    bool overlayLike = (exStyle & WS_EX_LAYERED) != 0 ||
                                       (exStyle & WS_EX_TRANSPARENT) != 0;

                    bool suspiciousTitle = suspiciousWindowKeywords.Any(k =>
                        title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!overlayLike || !suspiciousTitle)
                        return true;

                    string proc = TryGetProcessName((int)pid);

                    if (overlayCooldown.ContainsKey(proc) &&
                        DateTime.UtcNow - overlayCooldown[proc] < overlayDelay)
                    {
                        return true;
                    }

                    overlayCooldown[proc] = DateTime.UtcNow;

                    int existingScore = 0;
                    processScores.TryGetValue((int)pid, out existingScore);

                    existingScore += proc.Equals("overlay", StringComparison.OrdinalIgnoreCase) ? 70 : 55;
                    processScores[(int)pid] = existingScore;

                    string severity =
                        existingScore >= 100 ? "[HIGH RISK OVERLAY]" :
                        existingScore >= 70 ? "[SUSPICIOUS OVERLAY]" :
                        "[OVERLAY]";

                    Log($"{severity} {proc} | PID {(int)pid} | Score {existingScore} | Window: {title}");
                }
                catch
                {
                }

                return true;
            }, IntPtr.Zero);
        }

        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            logs.Add(line);
        }

        private async Task SendFinalWebhook()
        {
            if (finalWebhookSent)
                return;

            finalWebhookSent = true;

            try
            {
                StringBuilder logText = new StringBuilder();
                foreach (string line in logs)
                    logText.AppendLine(line);

                string header =
                    "Discord Username: " + discordUsername + "\n" +
                    "Roblox Username: " + robloxUsername + "\n" +
                    "Session ID: " + sessionId + "\n" +
                    "Time: " + startTime.ToString("yyyy-MM-dd HH:mm:ss") + "\n\n" +
                    "Anti-Cheat Log\n";

                string logBody = logText.ToString();

                int maxContentLength = 1850;
                string wrappedLogs = "```" + "\n" + logBody + "```";
                string message = header + wrappedLogs;

                if (message.Length > maxContentLength)
                {
                    int allowedLogLength = Math.Max(0, maxContentLength - header.Length - 8);
                    if (logBody.Length > allowedLogLength)
                        logBody = logBody.Substring(0, allowedLogLength);

                    wrappedLogs = "```" + "\n" + logBody + "\n```";
                    message = header + wrappedLogs;
                }

                var payload = new
                {
                    username = "Roblox Anti-Cheat",
                    content = message
                };

                string json = JsonSerializer.Serialize(payload);
                using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(WebhookUrl, content);

                setStatus(response.IsSuccessStatusCode ? "Session Ended" : "Session failed");
            }
            catch
            {
                setStatus("Session error");
            }
        }

        private string TryGetProcessName(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private string TryGetProcessPath(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return p.MainModule?.FileName ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private string TryGetFileSigner(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path == "Unknown")
                    return "Unknown";

                X509Certificate cert = X509Certificate.CreateFromSignedFile(path);
                return cert.Subject ?? "Unsigned/Unknown";
            }
            catch
            {
                return "Unsigned/Unknown";
            }
        }

        private bool IsSuspiciousPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "Unknown")
                return false;

            return suspiciousPathKeywords.Any(k =>
                path.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsUnsignedOrUnknownSigner(string signer)
        {
            if (string.IsNullOrWhiteSpace(signer))
                return true;

            return signer.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                   signer.Equals("Unsigned/Unknown", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
    }
}