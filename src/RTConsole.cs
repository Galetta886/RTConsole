// RTConsole - RimWorld Together (RTServer) 托管控制台
// 形态：本程序拉起 RTServer.exe 并接管其 stdin/stdout，同时在本机开一个 Web 面板
// 目标：.NET Framework 4.x，C# 5 语法（系统自带 csc 可编译）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RTConsole
{
    internal class UserInfo { public string Name; public string Ip; }

    internal static class Program
    {
        class Config
        {
            public string ServerDir;
            public int Port;
            public string Token;
            public bool AutoStart;
            public bool OpenBrowser;
        }

        static readonly object Sync = new object();
        static readonly object OperationSync = new object();
        static readonly ManualResetEvent ListenerReady = new ManualResetEvent(false);
        static Config cfg;
        static Process srvProc;
        static Mutex instanceMutex;
        static readonly List<string> LogLines = new List<string>();
        static readonly List<UserInfo> OnlineUsers = new List<UserInfo>();
        static readonly List<string> ConnectedIps = new List<string>();
        static readonly List<string> EventNames = new List<string>();
        static int eventListCountdown;
        static string logPath;
        const int LogCap = 1500;
        const int StateLines = 400;

        static void Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!AcquireSingleInstance(exeDir))
            {
                MessageBox.Show("RTConsole 已在当前安装目录运行。请使用已经打开的面板。", "RTConsole",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            LoadConfig(exeDir, args);
            Directory.CreateDirectory(Path.Combine(exeDir, "logs"));
            logPath = Path.Combine(exeDir, "logs", "rtconsole-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

            AddLog("[RTConsole] 启动 | 服务器目录: " + ServerRoot());

            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                try { StopServer(2000); } catch { }
            };

            var t = new Thread(ListenLoop);
            t.IsBackground = true;
            t.Start();

            if (!ListenerReady.WaitOne(5000))
            {
                AddLog("[RTConsole] 面板监听启动超时");
                return;
            }

            if (cfg.AutoStart) TryStartServer(false);
            if (cfg.OpenBrowser)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://localhost:" + cfg.Port + "/#token=" + cfg.Token) { UseShellExecute = true });
                }
                catch (Exception ex) { AddLog("[RTConsole] 打开浏览器失败: " + ex.Message); }
            }

            Thread.Sleep(Timeout.Infinite);
        }

        // ---------- 配置 ----------

        static bool AcquireSingleInstance(string exeDir)
        {
            string full = Path.GetFullPath(exeDir).TrimEnd('\\', '/').ToUpperInvariant();
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full));
            var name = new StringBuilder("Local\\RTConsole-");
            for (int i = 0; i < 12; i++) name.Append(hash[i].ToString("x2"));
            bool created;
            instanceMutex = new Mutex(true, name.ToString(), out created);
            return created;
        }

        static void LoadConfig(string exeDir, string[] args)
        {
            cfg = new Config();
            cfg.ServerDir = File.Exists(Path.Combine(exeDir, "RTServer.exe"))
                ? "."
                : "Server-win-x64";
            cfg.Port = 2678;
            cfg.Token = Guid.NewGuid().ToString("N");
            cfg.AutoStart = true;
            cfg.OpenBrowser = true;

            string path = Path.Combine(exeDir, "rtconsole.json");
            if (File.Exists(path))
            {
                string txt = File.ReadAllText(path);
                string v = JsonGetString(txt, "ServerDir"); if (!string.IsNullOrEmpty(v)) cfg.ServerDir = v.Trim('"');
                v = JsonGetString(txt, "Token"); if (!string.IsNullOrEmpty(v)) cfg.Token = v.Trim('"');
                int n; if (int.TryParse(JsonGetRaw(txt, "Port"), out n) && n > 0) cfg.Port = n;
                bool b;
                if (bool.TryParse(JsonGetRaw(txt, "AutoStart"), out b)) cfg.AutoStart = b;
                if (bool.TryParse(JsonGetRaw(txt, "OpenBrowser"), out b)) cfg.OpenBrowser = b;
            }

            foreach (string a in args)
            {
                if (a == "--no-browser") cfg.OpenBrowser = false;
                else if (a == "--no-autostart") cfg.AutoStart = false;
                else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    int n; if (int.TryParse(a.Substring(7), out n) && n > 0) cfg.Port = n;
                }
                else if (a.StartsWith("--server=", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.ServerDir = a.Substring(9);
                }
            }

            SaveConfig(path);
        }

        static string ServerRoot()
        {
            string value = string.IsNullOrWhiteSpace(cfg.ServerDir) ? "Server-win-x64" : cfg.ServerDir.Trim();
            if (!Path.IsPathRooted(value)) value = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, value);
            return Path.GetFullPath(value);
        }

        static void EnsureServerRoot()
        {
            string root = ServerRoot();
            if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "RTServer.exe")))
                throw new DirectoryNotFoundException("服务器目录无效或缺少 RTServer.exe: " + root);
        }

        static void SaveConfig(string path)
        {
            string json = "{\r\n"
                + "  \"ServerDir\": " + EscapeJson(cfg.ServerDir) + ",\r\n"
                + "  \"Port\": " + cfg.Port + ",\r\n"
                + "  \"Token\": " + EscapeJson(cfg.Token) + ",\r\n"
                + "  \"AutoStart\": " + (cfg.AutoStart ? "true" : "false") + ",\r\n"
                + "  \"OpenBrowser\": " + (cfg.OpenBrowser ? "true" : "false") + "\r\n"
                + "}\r\n";
            try { File.WriteAllText(path, json, new UTF8Encoding(false)); } catch { }
        }

        // ---------- 服务器进程 ----------

        static int ServerPort()
        {
            try
            {
                string p = Path.Combine(ServerRoot(), "Configs", "ServerConfig.json");
                if (File.Exists(p))
                {
                    Match m = Regex.Match(File.ReadAllText(p), "\"Port\"\\s*:\\s*(\\d+)");
                    if (m.Success) return int.Parse(m.Groups[1].Value);
                }
            }
            catch { }
            return 25555;
        }

        static bool PortBusy(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect("127.0.0.1", port, null, null);
                    if (r.AsyncWaitHandle.WaitOne(400))
                    {
                        c.EndConnect(r);
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        static DateTime extProbeAt = DateTime.MinValue;
        static bool extProbeCached;

        // 自己托管的服务器活着就直接算占用；只在服务器停止时才探测外部实例（5 秒缓存），
        // 避免面板轮询时反复 TCP 触碰游戏端口、在服务器日志里刷假 [Connect]。
        static bool ExternalRunning(int gamePort)
        {
            if (ServerAlive()) return true;
            if ((DateTime.Now - extProbeAt).TotalSeconds < 5) return extProbeCached;
            extProbeCached = PortBusy(gamePort);
            extProbeAt = DateTime.Now;
            return extProbeCached;
        }

        static bool ServerAlive()
        {
            Process p = srvProc;
            return p != null && !p.HasExited;
        }

        static void TryStartServer(bool throwErrors)
        {
            if (ServerAlive()) { if (throwErrors) throw new InvalidOperationException("服务器已在本控制台运行"); return; }

            string exe = Path.Combine(ServerRoot(), "RTServer.exe");
            if (!File.Exists(exe))
            {
                string msg = "找不到 " + exe + "，请检查 rtconsole.json 的 ServerDir";
                AddLog("[RTConsole] " + msg);
                if (throwErrors) throw new FileNotFoundException(msg);
                return;
            }

            int gamePort = ServerPort();
            if (PortBusy(gamePort))
            {
                string msg = "端口 " + gamePort + " 已被占用（可能已有一个 RTServer 窗口在运行），不重复启动";
                AddLog("[RTConsole] " + msg);
                if (throwErrors) throw new InvalidOperationException(msg);
                return;
            }

            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.WorkingDirectory = ServerRoot();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            var proc = new Process();
            proc.StartInfo = psi;
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { HandleLine(e.Data); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { HandleLine(e.Data); };
            proc.Exited += delegate
            {
                int code = -1;
                try { code = proc.ExitCode; } catch { }
                lock (Sync)
                {
                    AddLog("[RTConsole] 服务器进程已退出 (code=" + code + ")");
                    OnlineUsers.Clear();
                    ConnectedIps.Clear();
                }
            };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                AddLog("[RTConsole] 启动服务器失败: " + ex.Message);
                if (throwErrors) throw;
                return;
            }

            srvProc = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            AddLog("[RTConsole] 服务器已启动 (PID=" + proc.Id + ", 端口=" + gamePort + ")");
        }

        static void StopServer(int killAfterMs)
        {
            Process p = srvProc;
            if (p == null || p.HasExited) return;
            try { p.StandardInput.WriteLine("quit"); } catch { }
            if (!p.WaitForExit(Math.Max(1000, killAfterMs)))
            {
                try { p.Kill(); } catch { }
                p.WaitForExit(5000);
            }
        }

        static bool SendCommand(string cmd)
        {
            cmd = (cmd ?? "").Trim();
            if (cmd.Length == 0) return false;
            AddLog("> " + cmd);
            eventListCountdown = cmd.Equals("eventlist", StringComparison.OrdinalIgnoreCase) ? 60 : 0;
            Process p = srvProc;
            if (p == null || p.HasExited)
            {
                AddLog("[RTConsole] 服务器未运行，命令未发送");
                return false;
            }
            try
            {
                p.StandardInput.WriteLine(cmd);
                return true;
            }
            catch (Exception ex)
            {
                AddLog("[RTConsole] 命令发送失败: " + ex.Message);
                return false;
            }
        }

        // ---------- 日志与解析 ----------

        static void AddLog(string line)
        {
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line;
            lock (Sync)
            {
                LogLines.Add(stamped);
                if (LogLines.Count > LogCap) LogLines.RemoveRange(0, LogLines.Count - LogCap);
            }
            try { File.AppendAllText(logPath, stamped + "\r\n", new UTF8Encoding(false)); } catch { }
        }

        static void HandleLine(string line)
        {
            if (line == null || line.Length == 0) return;
            // 服务器输出自带 "[HH:mm:ss] | " 前缀，去掉避免与本程序时间戳重复
            line = Regex.Replace(line, "^\\[\\d{2}:\\d{2}:\\d{2}\\]\\s*\\|\\s*", "");
            AddLog(line);
            ParseUserLine(line, "[Log in] > ", true);
            ParseUserLine(line, "[Log out] > ", false);
            ParseIpLine(line, "[Connect] > ", true);
            ParseIpLine(line, "[Disconnect] > ", false);

            if (eventListCountdown > 0)
            {
                eventListCountdown--;
                // 时间戳前缀已被剥掉，事件行是裸事件名或 "Name - 描述" 两种形态
                Match m = Regex.Match(line, "^([A-Za-z][A-Za-z0-9_]{2,40})(?:\\s+-|\\s*$)");
                if (m.Success)
                {
                    lock (Sync)
                    {
                        if (!EventNames.Contains(m.Groups[1].Value))
                        {
                            EventNames.Add(m.Groups[1].Value);
                            if (EventNames.Count > 300) EventNames.RemoveAt(0);
                        }
                    }
                }
                else if (line.Contains("---")) { eventListCountdown = 0; }
            }
        }

        static void ParseUserLine(string line, string marker, bool login)
        {
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return;
            string rest = line.Substring(i + marker.Length).Trim();
            string name = rest, ip = "";
            int sep = rest.IndexOf(" | ", StringComparison.Ordinal);
            if (sep >= 0)
            {
                name = rest.Substring(0, sep).Trim();
                ip = rest.Substring(sep + 3).Trim();
            }
            lock (Sync)
            {
                for (int k = OnlineUsers.Count - 1; k >= 0; k--)
                    if (OnlineUsers[k].Name == name) OnlineUsers.RemoveAt(k);
                if (login && name.Length > 0) OnlineUsers.Add(new UserInfo { Name = name, Ip = ip });
            }
        }

        static void ParseIpLine(string line, string marker, bool connect)
        {
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return;
            string ip = line.Substring(i + marker.Length).Trim();
            lock (Sync)
            {
                ConnectedIps.Remove(ip);
                if (connect && ip.Length > 0) ConnectedIps.Add(ip);
            }
        }

        // ---------- HTTP ----------

        static void ListenLoop()
        {
            int attempt = 0;
            while (attempt < 10)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + cfg.Port + "/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    AddLog("[RTConsole] 端口 " + cfg.Port + " 监听失败 (" + ex.Message + ")，尝试下一个端口");
                    cfg.Port++;
                    attempt++;
                    continue;
                }
                SaveConfig(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rtconsole.json"));
                AddLog("[RTConsole] 面板地址: http://localhost:" + cfg.Port + "/（访问令牌仅保存在 rtconsole.json）");
                ListenerReady.Set();
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); }
                    catch { break; }
                    ThreadPool.QueueUserWorkItem(delegate { try { Handle(ctx); } catch (Exception ex) { AddLog("[RTConsole] 请求处理异常: " + ex.Message); } });
                }
            }
            ListenerReady.Set();
        }

        static void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            string path = req.Url.AbsolutePath;
            try
            {
                string token = req.QueryString["token"];
                if (string.IsNullOrEmpty(token)) token = req.Headers["X-Token"];
                if (path == "/" && req.HttpMethod == "GET")
                {
                    string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "panel.html");
                    if (File.Exists(htmlPath))
                    {
                        byte[] html = File.ReadAllBytes(htmlPath);
                        res.ContentType = "text/html; charset=utf-8";
                        res.ContentLength64 = html.Length;
                        res.OutputStream.Write(html, 0, html.Length);
                    }
                    else
                    {
                        WriteJson(res, 500, "{\"error\":\"panel.html 缺失\"}");
                    }
                    return;
                }
                if (!string.Equals(token, cfg.Token, StringComparison.Ordinal))
                {
                    WriteJson(res, 401, "{\"error\":\"token 无效\"}");
                    return;
                }

                if (path == "/api/state" && req.HttpMethod == "GET") { ApiState(res); return; }
                if (path == "/api/backups" && req.HttpMethod == "GET") { ApiBackups(res); return; }
                if (path == "/api/profiles" && req.HttpMethod == "GET") { ApiProfiles(res); return; }
                if (req.HttpMethod == "POST")
                {
                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                    if (!Monitor.TryEnter(OperationSync))
                    {
                        WriteJson(res, 409, "{\"error\":\"已有管理操作正在执行，请稍后重试\"}");
                        return;
                    }
                    try
                    {
                        switch (path)
                        {
                            case "/api/cmd": ApiCmd(res, body); return;
                            case "/api/start": ApiStart(res); return;
                            case "/api/stop": ApiStop(res); return;
                            case "/api/backup": ApiBackup(res); return;
                            case "/api/restore": ApiRestore(res, body); return;
                            case "/api/profile/save": ApiProfileSave(res, body); return;
                            case "/api/profile/switch": ApiProfileSwitch(res, body); return;
                            case "/api/profile/delete": ApiProfileDelete(res, body); return;
                            case "/api/world/reset": ApiWorldReset(res, body); return;
                            case "/api/shutdown": ApiShutdown(res); return;
                        }
                    }
                    finally { Monitor.Exit(OperationSync); }
                }
                WriteJson(res, 404, "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                try { WriteJson(res, 500, "{\"error\":" + EscapeJson(ex.Message) + "}"); } catch { }
            }
        }

        static void ApiState(HttpListenerResponse res)
        {
            bool alive; int gamePort = ServerPort(); bool extBusy = ExternalRunning(gamePort);
            var users = new List<UserInfo>(); var ips = new List<string>(); var events = new List<string>(); var lines = new List<string>();
            lock (Sync)
            {
                alive = srvProc != null && !srvProc.HasExited;
                users.AddRange(OnlineUsers);
                ips.AddRange(ConnectedIps);
                events.AddRange(EventNames);
                int start = Math.Max(0, LogLines.Count - StateLines);
                for (int i = start; i < LogLines.Count; i++) lines.Add(LogLines[i]);
            }
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"running\":").Append(alive ? "true" : "false");
            sb.Append(",\"extRunning\":").Append(extBusy ? "true" : "false");
            sb.Append(",\"port\":").Append(gamePort);
            sb.Append(",\"mods\":").Append(CurrentModCount());
            sb.Append(",\"online\":[");
            for (int i = 0; i < users.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(EscapeJson(users[i].Name)).Append(",\"ip\":").Append(EscapeJson(users[i].Ip)).Append("}");
            }
            sb.Append("],\"ips\":[");
            for (int i = 0; i < ips.Count; i++) { if (i > 0) sb.Append(","); sb.Append(EscapeJson(ips[i])); }
            sb.Append("],\"events\":[");
            for (int i = 0; i < events.Count; i++) { if (i > 0) sb.Append(","); sb.Append(EscapeJson(events[i])); }
            sb.Append("],\"lines\":[");
            for (int i = 0; i < lines.Count; i++) { if (i > 0) sb.Append(","); sb.Append(EscapeJson(lines[i])); }
            sb.Append("]}");
            WriteJson(res, 200, sb.ToString());
        }

        static void ApiCmd(HttpListenerResponse res, string body)
        {
            string cmd = (JsonGetString(body, "cmd") ?? "").Trim();
            string confirm = JsonGetString(body, "confirm") ?? "";
            if (cmd.Length == 0) { WriteJson(res, 400, "{\"error\":\"命令为空\"}"); return; }
            string first = cmd.Split(' ')[0].ToLowerInvariant();
            if (first == "resetserver" && confirm != "RESET")
            {
                WriteJson(res, 400, "{\"error\":\"resetserver 属于危险命令，需要在面板中输入 RESET 确认\"}");
                return;
            }
            bool ok = SendCommand(cmd);
            WriteJson(res, ok ? 200 : 409, ok ? "{\"ok\":true}" : "{\"error\":\"服务器未运行\"}");
        }

        static void ApiStart(HttpListenerResponse res)
        {
            try { TryStartServer(true); WriteJson(res, 200, "{\"ok\":true}"); }
            catch (Exception ex) { WriteJson(res, 409, "{\"error\":" + EscapeJson(ex.Message) + "}"); }
        }

        static void ApiStop(HttpListenerResponse res)
        {
            if (!ServerAlive() && ExternalRunning(ServerPort()))
            {
                WriteJson(res, 409, "{\"error\":\"本控制台未托管服务器，但有实例占用游戏端口，无法远程停止\"}");
                return;
            }
            StopServer(15000);
            WriteJson(res, 200, "{\"ok\":true}");
        }

        static void ApiBackup(HttpListenerResponse res)
        {
            bool ok = SendCommand("backup");
            WriteJson(res, ok ? 200 : 409, ok ? "{\"ok\":true}" : "{\"error\":\"服务器未运行\"}");
        }

        static void ApiRestore(HttpListenerResponse res, string body)
        {
            string file = (JsonGetString(body, "file") ?? "").Trim();
            string confirm = JsonGetString(body, "confirm") ?? "";
            if (confirm != "RESTORE") { WriteJson(res, 400, "{\"error\":\"需要在面板中输入 RESTORE 确认\"}"); return; }
            if (ServerAlive() || ExternalRunning(ServerPort()))
            {
                WriteJson(res, 409, "{\"error\":\"还原前必须先停止服务器\"}");
                return;
            }
            string name = Path.GetFileName(file);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { WriteJson(res, 400, "{\"error\":\"只能还原 zip\"}"); return; }
            string zipPath = Path.Combine(ServerRoot(), "Backups", "Servers", name);
            if (!File.Exists(zipPath)) { WriteJson(res, 404, "{\"error\":\"备份不存在: " + EscapeJson(name) + "\"}"); return; }
            try
            {
                int n = ApplySnapshotZip(zipPath);
                AddLog("[RTConsole] 已从 " + name + " 还原 " + n + " 个文件（还原前已自动保存 PreRestore 快照）");
                WriteJson(res, 200, "{\"ok\":true,\"restored\":" + n + "}");
            }
            catch (Exception ex)
            {
                WriteJson(res, 500, "{\"error\":" + EscapeJson(ex.Message) + "}");
            }
        }

        // ---------- 存档档案（按模组组合切换的世界+存档快照） ----------

        static string ProfilesDir()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        }

        static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = Regex.Replace(s, "[\\\\/:*?\"<>|]", "_").Trim();
            if (t.Length > 60) t = t.Substring(0, 60);
            return t;
        }

        static int CountModsInZip(string zipPath)
        {
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry e = zip.GetEntry("Configs\\ModConfig.json");
                    if (e == null) e = zip.GetEntry("Configs/ModConfig.json");
                    if (e == null) return -1;
                    using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                    {
                        return Regex.Matches(sr.ReadToEnd(), "\"ModID\"").Count;
                    }
                }
            }
            catch { return -1; }
        }

        static int CurrentModCount()
        {
            try
            {
                string p = Path.Combine(ServerRoot(), "Configs", "ModConfig.json");
                if (File.Exists(p)) return Regex.Matches(File.ReadAllText(p), "\"ModID\"").Count;
            }
            catch { }
            return -1;
        }

        // 把当前 Assets+Configs 打包为快照 zip（服务器停止时使用；也用于 PreRestore 保底）
        static string CreateSnapshotZip(string destZip)
        {
            EnsureServerRoot();
            string backupDir = Path.Combine(ServerRoot(), "Backups", "Servers");
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string staging = Path.Combine(backupDir, "tmp_snap_" + stamp + "_" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            try
            {
                CopyDir(Path.Combine(ServerRoot(), "Assets"), Path.Combine(staging, "Assets"));
                CopyDir(Path.Combine(ServerRoot(), "Configs"), Path.Combine(staging, "Configs"));
                string destDir = Path.GetDirectoryName(destZip);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                if (File.Exists(destZip)) File.Delete(destZip);
                ZipFile.CreateFromDirectory(staging, destZip, CompressionLevel.Optimal, false);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            return destZip;
        }

        static void WipeAssetsConfigs()
        {
            string a = Path.Combine(ServerRoot(), "Assets");
            string c = Path.Combine(ServerRoot(), "Configs");
            if (Directory.Exists(a)) Directory.Delete(a, true);
            if (Directory.Exists(c)) Directory.Delete(c, true);
        }

        static int ExtractAssetsConfigs(string zipPath, string targetRoot)
        {
            const int MaxEntries = 250000;
            const long MaxExpandedBytes = 25L * 1024L * 1024L * 1024L;
            int count = 0;
            long expandedBytes = 0;
            bool hasServerConfig = false;
            string root = Path.GetFullPath(targetRoot);
            string rootPrefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string assetsPrefix = Path.GetFullPath(Path.Combine(root, "Assets")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string configsPrefix = Path.GetFullPath(Path.Combine(root, "Configs")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("快照文件数量超过安全上限");
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string fn = e.FullName.Replace('/', '\\');
                    if (string.IsNullOrWhiteSpace(fn)) continue;
                    if (Path.IsPathRooted(fn) || fn.StartsWith("\\", StringComparison.Ordinal) || fn.IndexOf(':') >= 0)
                        throw new InvalidDataException("快照包含绝对路径: " + fn);
                    bool underAssets = fn.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase);
                    bool underConfigs = fn.StartsWith("Configs\\", StringComparison.OrdinalIgnoreCase);
                    if (!underAssets && !underConfigs)
                        throw new InvalidDataException("快照包含 Assets/Configs 之外的文件: " + fn);
                    if (fn.EndsWith("\\")) continue;
                    string dest = Path.GetFullPath(Path.Combine(root, fn));
                    if (!dest.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        || (underAssets && !dest.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                        || (underConfigs && !dest.StartsWith(configsPrefix, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("快照路径越界，已拒绝: " + fn);
                    if (!destinations.Add(dest)) throw new InvalidDataException("快照包含重复路径: " + fn);
                    expandedBytes += e.Length;
                    if (expandedBytes > MaxExpandedBytes) throw new InvalidDataException("快照解压后体积超过安全上限");
                    string dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    e.ExtractToFile(dest, true);
                    if (fn.Equals("Configs\\ServerConfig.json", StringComparison.OrdinalIgnoreCase)) hasServerConfig = true;
                    count++;
                }
            }
            if (count == 0) throw new InvalidDataException("快照中没有可还原文件");
            if (!hasServerConfig) throw new InvalidDataException("快照缺少 Configs/ServerConfig.json，拒绝覆盖当前世界");
            return count;
        }

        // 替换式应用快照：先保底快照当前状态 → 清空 Assets+Configs → 解压 → 失败自动回滚
        static int ApplySnapshotZip(string zipPath)
        {
            EnsureServerRoot();
            string backupDir = Path.Combine(ServerRoot(), "Backups", "Servers");
            string staging = Path.Combine(backupDir, "tmp_restore_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                // 先在隔离目录完整校验并解压，成功后才接触当前世界。
                int count = ExtractAssetsConfigs(zipPath, staging);
                string safety = CreateSnapshotZip(Path.Combine(backupDir, "PreRestore_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".zip"));
                WipeAssetsConfigs();
                try
                {
                    CopyDir(Path.Combine(staging, "Assets"), Path.Combine(ServerRoot(), "Assets"));
                    CopyDir(Path.Combine(staging, "Configs"), Path.Combine(ServerRoot(), "Configs"));
                    return count;
                }
                catch
                {
                    AddLog("[RTConsole] 应用快照失败，自动回滚到 PreRestore 保底快照");
                    WipeAssetsConfigs();
                    ExtractAssetsConfigs(safety, ServerRoot());
                    throw;
                }
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        // ---------- 重开新世界 ----------

        static void ApiWorldReset(HttpListenerResponse res, string body)
        {
            string confirm = JsonGetString(body, "confirm") ?? "";
            string keepUsers = JsonGetString(body, "keepUsers") ?? "true";
            if (confirm != "NEWWORLD") { WriteJson(res, 400, "{\"error\":\"需要在面板中输入 NEWWORLD 确认\"}"); return; }
            try
            {
                if (ServerAlive())
                {
                    AddLog("[RTConsole] 重开世界前自动停止服务器");
                    StopServer(15000);
                }
                if (ExternalRunning(ServerPort()))
                {
                    WriteJson(res, 409, "{\"error\":\"有外部服务器实例占用端口，无法重开\"}");
                    return;
                }

                string assets = Path.Combine(ServerRoot(), "Assets");
                string backupDir = Path.Combine(ServerRoot(), "Backups", "Servers");
                string safety = CreateSnapshotZip(Path.Combine(backupDir, "PreNewWorld_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".zip"));
                AddLog("[RTConsole] 重开世界：保底快照已存 " + Path.GetFileName(safety));

                // 世界数据：目录清空内容后保留空目录
                string[] worldDirs = new string[] { "Maps", "Settlements", "Sites", "WorldObjects", "Guilds", "Saves" };
                foreach (string d in worldDirs)
                {
                    string p = Path.Combine(assets, d);
                    if (Directory.Exists(p))
                    {
                        foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); } catch { }
                        }
                        foreach (string sub in Directory.GetDirectories(p))
                        {
                            try { Directory.Delete(sub, true); } catch { }
                        }
                    }
                    else Directory.CreateDirectory(p);
                }
                // 世界级文件
                string[] worldFiles = new string[] { "Rivers.json", "Roads.json", "WorldValuesFile.json", "Market.json", "Leaderboard.json" };
                foreach (string f in worldFiles)
                {
                    string p = Path.Combine(assets, f);
                    if (File.Exists(p)) File.Delete(p);
                }
                // 玩家账号（可选保留）
                if (!keepUsers.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    string u = Path.Combine(assets, "Users");
                    if (Directory.Exists(u))
                    {
                        foreach (string f in Directory.GetFiles(u)) { try { File.Delete(f); } catch { } }
                    }
                    AddLog("[RTConsole] 重开世界：玩家账号已清空（需重新注册）");
                }

                AddLog("[RTConsole] 世界数据已清空，重启服务器生成全新世界");
                TryStartServer(false);
                bool started = ServerAlive();
                WriteJson(res, 200, "{\"ok\":true,\"started\":" + (started ? "true" : "false") + ",\"snapshot\":" + EscapeJson(Path.GetFileName(safety)) + "}");
            }
            catch (Exception ex)
            {
                WriteJson(res, 500, "{\"error\":" + EscapeJson(ex.Message) + "}");
            }
        }

        static void ApiProfiles(HttpListenerResponse res)
        {
            var list = new List<FileInfo>();
            string dir = ProfilesDir();
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.zip")) list.Add(new FileInfo(f));
            }
            catch { }
            list.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"curMods\":").Append(CurrentModCount()).Append(",\"profiles\":[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(EscapeJson(list[i].Name));
                sb.Append(",\"mb\":").Append((list[i].Length / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"t\":").Append(EscapeJson(list[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                sb.Append(",\"mods\":").Append(CountModsInZip(list[i].FullName));
                sb.Append("}");
            }
            sb.Append("]}");
            WriteJson(res, 200, sb.ToString());
        }

        static void ApiProfileSave(HttpListenerResponse res, string body)
        {
            string name = SanitizeName(JsonGetString(body, "name") ?? "");
            if (name.Length == 0) { WriteJson(res, 400, "{\"error\":\"档案名不能为空\"}"); return; }
            string dest = Path.Combine(ProfilesDir(), name + ".zip");
            try
            {
                if (!Directory.Exists(ProfilesDir())) Directory.CreateDirectory(ProfilesDir());
                if (ServerAlive())
                {
                    DateTime t0 = DateTime.Now.AddSeconds(-2);
                    SendCommand("backup");
                    string src = null;
                    long lastSize = -1;
                    for (int i = 0; i < 60; i++)
                    {
                        Thread.Sleep(500);
                        string cand = NewestZipAfter(Path.Combine(ServerRoot(), "Backups", "Servers"), t0);
                        if (cand != null)
                        {
                            long sz = new FileInfo(cand).Length;
                            if (sz > 0 && sz == lastSize) { src = cand; break; }
                            lastSize = sz;
                        }
                    }
                    if (src == null) { WriteJson(res, 500, "{\"error\":\"服务器 30 秒内未产出备份文件\"}"); return; }
                    MoveOverwrite(src, dest);
                }
                else
                {
                    CreateSnapshotZip(dest);
                }
                AddLog("[RTConsole] 已保存存档档案: " + name + " (模组数 " + CountModsInZip(dest) + ")");
                WriteJson(res, 200, "{\"ok\":true,\"mods\":" + CountModsInZip(dest) + "}");
            }
            catch (Exception ex)
            {
                WriteJson(res, 500, "{\"error\":" + EscapeJson(ex.Message) + "}");
            }
        }

        static string NewestZipAfter(string dir, DateTime t0)
        {
            string best = null;
            DateTime bestT = DateTime.MinValue;
            try
            {
                if (!Directory.Exists(dir)) return null;
                foreach (string f in Directory.GetFiles(dir, "*.zip"))
                {
                    DateTime t = File.GetLastWriteTime(f);
                    if (t >= t0 && t > bestT) { bestT = t; best = f; }
                }
            }
            catch { }
            return best;
        }

        static void MoveOverwrite(string src, string dest)
        {
            if (File.Exists(dest)) File.Delete(dest);
            try { File.Move(src, dest); }
            catch (IOException)
            {
                File.Copy(src, dest, true);
                File.Delete(src);
            }
        }

        static void ApiProfileSwitch(HttpListenerResponse res, string body)
        {
            string file = (JsonGetString(body, "file") ?? "").Trim();
            string confirm = JsonGetString(body, "confirm") ?? "";
            string autostart = JsonGetString(body, "autostart") ?? "true";
            if (confirm != "SWITCH") { WriteJson(res, 400, "{\"error\":\"需要在面板中输入 SWITCH 确认\"}"); return; }
            string name = Path.GetFileName(file);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { WriteJson(res, 400, "{\"error\":\"只能切换 zip 档案\"}"); return; }
            string zipPath = Path.Combine(ProfilesDir(), name);
            if (!File.Exists(zipPath)) { WriteJson(res, 404, "{\"error\":\"档案不存在: " + EscapeJson(name) + "\"}"); return; }
            try
            {
                if (ServerAlive())
                {
                    AddLog("[RTConsole] 切换档案前自动停止服务器");
                    StopServer(15000);
                }
                if (ExternalRunning(ServerPort()))
                {
                    WriteJson(res, 409, "{\"error\":\"有外部服务器实例占用端口，无法切换\"}");
                    return;
                }
                int n = ApplySnapshotZip(zipPath);
                AddLog("[RTConsole] 已切换到档案 " + name + "（替换 " + n + " 个文件，当前模组数 " + CurrentModCount() + "）");
                bool started = false;
                if (!autostart.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    TryStartServer(false);
                    started = ServerAlive();
                }
                WriteJson(res, 200, "{\"ok\":true,\"restored\":" + n + ",\"mods\":" + CurrentModCount() + ",\"started\":" + (started ? "true" : "false") + "}");
            }
            catch (Exception ex)
            {
                WriteJson(res, 500, "{\"error\":" + EscapeJson(ex.Message) + "}");
            }
        }

        static void ApiProfileDelete(HttpListenerResponse res, string body)
        {
            string file = (JsonGetString(body, "file") ?? "").Trim();
            string confirm = JsonGetString(body, "confirm") ?? "";
            if (confirm != "DELETE") { WriteJson(res, 400, "{\"error\":\"需要在面板中输入 DELETE 确认\"}"); return; }
            string name = Path.GetFileName(file);
            string zipPath = Path.Combine(ProfilesDir(), name);
            if (!File.Exists(zipPath)) { WriteJson(res, 404, "{\"error\":\"档案不存在\"}"); return; }
            File.Delete(zipPath);
            AddLog("[RTConsole] 已删除档案: " + name);
            WriteJson(res, 200, "{\"ok\":true}");
        }

        static void ApiShutdown(HttpListenerResponse res)
        {
            WriteJson(res, 200, "{\"ok\":true}");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(300);
                StopServer(3000);
                Environment.Exit(0);
            });
        }

        static void CopyDir(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\', '/');
                string target = Path.Combine(dst, rel);
                string tdir = Path.GetDirectoryName(target);
                if (!Directory.Exists(tdir)) Directory.CreateDirectory(tdir);
                File.Copy(f, target, true);
            }
        }

        static void ApiBackups(HttpListenerResponse res)
        {
            var list = new List<FileInfo>();
            string dir = Path.Combine(ServerRoot(), "Backups", "Servers");
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.zip")) list.Add(new FileInfo(f));
            }
            catch { }
            list.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"backups\":[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(EscapeJson(list[i].Name));
                sb.Append(",\"mb\":").Append((list[i].Length / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"t\":").Append(EscapeJson(list[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                sb.Append(",\"pre\":").Append(list[i].Name.StartsWith("PreRestore_", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]}");
            WriteJson(res, 200, sb.ToString());
        }

        // ---------- JSON 小工具 ----------

        static void WriteJson(HttpListenerResponse res, int code, string json)
        {
            res.StatusCode = code;
            res.ContentType = "application/json; charset=utf-8";
            res.Headers["Cache-Control"] = "no-store";
            byte[] b = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = b.Length;
            res.OutputStream.Write(b, 0, b.Length);
            res.OutputStream.Close();
        }

        static string EscapeJson(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        static string JsonGetString(string json, string key)
        {
            if (json == null) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return UnescapeJson(m.Groups[1].Value);
        }

        static string JsonGetRaw(string json, string key)
        {
            if (json == null) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?([^,\\r\\n}\"]+)\"?");
            if (!m.Success) return "";
            return m.Groups[1].Value.Trim();
        }

        static string UnescapeJson(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char d = s[++i];
                    switch (d)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(d); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
