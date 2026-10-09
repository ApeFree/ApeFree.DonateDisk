using ApeFree.DonateDisk.Slave;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.WinSlave
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.Title = "ApeFree.DonateDisk.WinSlave - 远程磁盘主动分享客户端";

            PrintBanner();

            var options = ParseArguments(args);
            if (options == null)
            {
                // 若命令行未提供完整参数，进入交互式引导输入
                options = PromptInteractiveOptions();
                if (options == null)
                {
                    return 1;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== 运行配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  Master 主机:      {options.TargetHost}:{options.TargetPort}");
            Console.WriteLine($"  分享物理目录:    {options.LocalDirectory}");
            Console.WriteLine($"  卷标显示名称:    {options.VolumeLabel ?? Path.GetFileName(options.LocalDirectory.TrimEnd('\\', '/'))}");
            Console.WriteLine($"  只读保护模式:    {(options.IsReadOnly ? "已开启 [只读]" : "已关闭 [可读写]")}");
            Console.WriteLine("================================================");
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[系统] 正在安全停止 Slave 节点并释放本地文件资源...");
                cts.Cancel();
            };

            using var slaveNode = new DiskSlaveNode(options);

            slaveNode.Connected += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [✓ 已连接] 成功连入 Master 端并就绪发布磁盘服务！");
                Console.ResetColor();
            };

            slaveNode.Disconnected += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [! 已断开] 与 Master 端的网络连接已中断，本地句柄已自动清理。");
                Console.ResetColor();
            };

            slaveNode.FileChanged += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                var detail = string.IsNullOrEmpty(e.OldRelativePath)
                    ? e.RelativePath
                    : $"{e.OldRelativePath} -> {e.RelativePath}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [本地变动] [{e.ChangeType}] {detail}");
                Console.ResetColor();
            };

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在连接 Master ({options.TargetHost}:{options.TargetPort})...");

            try
            {
                var success = await slaveNode.StartAsync(cts.Token);
                if (!success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [× 连接失败] 无法连接到 Master 端 ({options.TargetHost}:{options.TargetPort})。请检查 Master 是否已启动或网络端口是否通畅。");
                    Console.ResetColor();
                    return 2;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 磁盘捐赠已启动！按 Ctrl+C 可停止分享并安全退出。");
                Console.ResetColor();

                // 监控循环
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 发生未处理异常: {ex.Message}");
                Console.ResetColor();
                return 3;
            }
            finally
            {
                slaveNode.Stop();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Slave 节点已退出。");
            }

            return 0;
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(@"
    ___              ______              ___                 __      ___       __  
   /   |  ____  ___ / ____/_______  ___ /   |  ____  ____   / /___  /   | ____/ /__
  / /| | / __ \/ _ \ /_  / ___/ _ \/ _ \ /| | / __ \/ __ \ / / __ \/ /| |/ __  / _ \
 / ___ |/ /_/ /  __/ __// /  /  __/  __/ ___ |/ /_/ / /_/ // / /_/ / ___ / /_/ /  __/
/_/  |_/ .___/\___/_/  /_/   \___/\___/_/  |_/ .___/\____//_/\____/_/  |_\__,_/\___/ 
      /_/                                   /_/       DonateDisk (Slave Node)      
            ");
            Console.ResetColor();
        }

        private static DiskSlaveOptions? ParseArguments(string[] args)
        {
            if (args.Length == 0) return null;

            string host = "127.0.0.1";
            int port = 9821;
            string? dir = null;
            string? label = null;
            bool isReadOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if ((arg == "-h" || arg == "--host") && i + 1 < args.Length)
                {
                    host = args[++i];
                }
                else if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                {
                    port = p;
                    i++;
                }
                else if ((arg == "-d" || arg == "--dir") && i + 1 < args.Length)
                {
                    dir = args[++i];
                }
                else if ((arg == "-n" || arg == "--name") && i + 1 < args.Length)
                {
                    label = args[++i];
                }
                else if (arg == "-r" || arg == "--readonly")
                {
                    isReadOnly = true;
                }
                else if (arg == "--help" || arg == "-?")
                {
                    PrintHelp();
                    Environment.Exit(0);
                }
            }

            if (string.IsNullOrWhiteSpace(dir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[错误] 必须通过 -d 或 --dir 指定待分享的本地物理目录。");
                Console.ResetColor();
                PrintHelp();
                return null;
            }

            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[错误] 指定的本地目录不存在: {fullDir}");
                Console.ResetColor();
                return null;
            }

            return new DiskSlaveOptions
            {
                TargetHost = host,
                TargetPort = port,
                LocalDirectory = fullDir,
                VolumeLabel = label,
                IsReadOnly = isReadOnly
            };
        }

        private static DiskSlaveOptions? PromptInteractiveOptions()
        {
            Console.WriteLine("检测到未指定启动参数，进入交互式引导模式：");
            Console.WriteLine();

            Console.Write("请输入待分享的本地文件夹物理路径 (例如 D:\\MyShare): ");
            var dir = Console.ReadLine()?.Trim('\"', ' ');
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("目录路径无效或不存在，操作已取消。");
                Console.ResetColor();
                return null;
            }

            Console.Write("请输入 Master 端主机 IP 地址 (默认 127.0.0.1): ");
            var hostInput = Console.ReadLine()?.Trim();
            var host = string.IsNullOrWhiteSpace(hostInput) ? "127.0.0.1" : hostInput;

            Console.Write("请输入 Master 端端口号 (默认 9821): ");
            var portInput = Console.ReadLine()?.Trim();
            int port = 9821;
            if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out var p))
            {
                port = p;
            }

            Console.Write("请输入自定义虚拟卷标名称 (直接回车使用目录名): ");
            var labelInput = Console.ReadLine()?.Trim();

            Console.Write("是否开启只读保护模式？(y/N): ");
            var roInput = Console.ReadLine()?.Trim();
            bool isReadOnly = string.Equals(roInput, "y", StringComparison.OrdinalIgnoreCase);

            return new DiskSlaveOptions
            {
                TargetHost = host,
                TargetPort = port,
                LocalDirectory = Path.GetFullPath(dir),
                VolumeLabel = string.IsNullOrWhiteSpace(labelInput) ? null : labelInput,
                IsReadOnly = isReadOnly
            };
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
用法:
  ApeFree.DonateDisk.WinSlave [选项]

选项:
  -h, --host <IP/域名>     Master 端主机 IP 地址（默认 127.0.0.1）
  -p, --port <端口>        Master 端服务端口（默认 9821）
  -d, --dir <本地路径>     待分享给 Master 的本地物理文件夹路径（必需）
  -n, --name <卷标名称>    挂载后显示的虚拟磁盘名称（默认使用目录名）
  -r, --readonly           以只读保护模式分享（禁止远端修改和删除文件）
  -?, --help               显示本帮助信息

示例:
  ApeFree.DonateDisk.WinSlave -d ""D:\SharedData"" -h 192.168.1.100 -p 9821
  ApeFree.DonateDisk.WinSlave -d ""C:\PublicFiles"" -r
");
        }
    }
}
