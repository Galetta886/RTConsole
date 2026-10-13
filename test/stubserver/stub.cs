// 测试用假 RTServer：模拟 stdin 命令 + 标准日志格式输出
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

class Stub
{
    static void Out(string msg)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] | " + msg);
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Out("[Stub] fake RTServer started");
        Out("[Connect] > 9.9.9.9");
        Out("[Log in] > 测试玩家 | 9.9.9.9");

        string line;
        while ((line = Console.ReadLine()) != null)
        {
            Out("[echo] > " + line);
            if (line == "help")
            {
                Out("List of available commands: [3]");
                Out("----------------------------------------");
                Out("help - x");
                Out("list - x");
                Out("quit - x");
                Out("----------------------------------------");
            }
            else if (line == "list")
            {
                Out("Connected players: [1]");
                Out("----------------------------------------");
                Out("9.9.9.9");
                Out("----------------------------------------");
            }
            else if (line == "backup")
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", "Servers");
                Directory.CreateDirectory(dir);
                string zipPath = Path.Combine(dir, "Server_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".zip");
                using (var fs = new FileStream(zipPath, FileMode.Create))
                using (var arch = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry e = arch.CreateEntry("Assets\\Stub.json");
                    using (var w = new StreamWriter(e.Open(), Encoding.UTF8)) w.Write("{\"stub\":true}");
                    ZipArchiveEntry e2 = arch.CreateEntry("Configs\\ServerConfig.json");
                    using (var w = new StreamWriter(e2.Open(), Encoding.UTF8)) w.Write("{\"Port\":25555}");
                    ZipArchiveEntry e3 = arch.CreateEntry("Configs\\ModConfig.json");
                    using (var w = new StreamWriter(e3.Open(), Encoding.UTF8)) w.Write("{\"ModConfigs\":[{\"ModID\":\"111\"},{\"ModID\":\"222\"}]}");
                }
                Out("[Server backup] > " + zipPath);
            }
            else if (line == "eventlist")
            {
                Out("RaidEnemy");
                Out("Eclipse");
                Out("ColdSnap");
            }
            else if (line == "quit")
            {
                Out("[Stub] goodbye");
                break;
            }
        }
    }
}
