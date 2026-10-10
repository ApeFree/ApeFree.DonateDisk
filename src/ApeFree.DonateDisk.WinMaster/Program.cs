using ApeFree.DonateDisk.Master;
using DokanNet;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.WinMaster
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.Title = "ApeFree.DonateDisk.WinMaster - 远程磁盘自动挂载服务端";

            PrintBanner();

            // 1. 检查本地 Dokany 驱动与运行时环境
            CheckDokanyDriver();

            var options = ParseArguments(args);
            if (options == null)
            {
                options = PromptInteractiveOptions();
                if (options == null)
                {
                    return 1;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== 服务端配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  监听网络地址:    {options.ListenHost}:{options.ListenPort}");
            Console.WriteLine($"  首选驱动器盘符:  {options.PreferredDriveLetter}:\\ (向下自动顺延)");
            Console.WriteLine($"  挂载磁盘类型:    {(options.MountAsRemovable ? "可移动驱动器 (Removable)" : "本地固定磁盘 (Fixed)")}");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[系统] 正在关闭 Master 服务并安全卸载所有已挂载的磁盘...");
                cts.Cancel();
            };

            using var masterNode = new DiskMasterNode(options);

            masterNode.SlaveMounted += (s, session) =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [磁盘已挂载] 盘符 [{session.MountPoint}] 就绪！");
                Console.ResetColor();
                Console.WriteLine($"           - 客户端来源: {session.Client}");
                Console.WriteLine($"           - 卷标名称:   {session.VolumeInfo.VolumeLabel}");
                Console.WriteLine($"           - 驱动器路径: 可以在 Windows 资源管理器或 CMD 中访问 {session.MountPoint}");
                Console.WriteLine();
            };

            masterNode.SlaveUnmounted += (s, session) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [磁盘已卸载] 客户端断开连接，盘符 [{session.MountPoint}] 已自动安全卸载并释放。");
                Console.ResetColor();
                Console.WriteLine();
            };

            masterNode.MountFailed += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [× 挂载失败] 来自客户端 '{e.Client}' 的磁盘挂载出错: {e.Exception.Message}");
                Console.ResetColor();
                Console.WriteLine();
            };

            masterNode.RemoteFileChanged += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                var detail = string.IsNullOrEmpty(e.Change.OldRelativePath)
                    ? e.Change.RelativePath
                    : $"{e.Change.OldRelativePath} -> {e.Change.RelativePath}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [远端变动] [{e.Session.MountPoint}] [{e.Change.ChangeType}] {detail}");
                Console.ResetColor();
            };

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 TCP 服务端监听 ({options.ListenHost}:{options.ListenPort})...");

            try
            {
                await masterNode.StartAsync();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 服务端正在运行，等待 Slave 连入捐赠磁盘... (按 Ctrl+C 可停止服务)");
                Console.ResetColor();
                Console.WriteLine();

                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常终止
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 服务端发生异常: {ex.Message}");
                Console.ResetColor();
                return 2;
            }
            finally
            {
                await masterNode.StopAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Master 服务端已完全停止。");
            }

            return 0;
        }

        private static void CheckDokanyDriver()
        {
            try
            {
                var dokan = new Dokan(null!);
                var ver = dokan.DriverVersion;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[环境自检] 已检测到 Dokany 内核驱动就绪 (版本: {ver})。");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[环境警告] 检测到当前系统可能未正确安装 Dokany 驱动或版本不匹配：");
                Console.WriteLine($"           详情: {ex.Message}");
                Console.WriteLine("           若 Slave 连接后挂载失败，请先安装 Dokany 运行时：");
                Console.WriteLine("           下载地址: https://github.com/dokan-dev/dokany/releases");
                Console.WriteLine();
                Console.ResetColor();
            }
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(@"
    ___               ______                 ____                    __       ____  _      __  
   /   |  ____  ___  / ____/_______  ___    / __ \____  ____  ____ _/ /____  / __ \(_)____/ /__
  / /| | / __ \/ _ \/ /_  / ___/ _ \/ _ \  / / / / __ \/ __ \/ __ `/ __/ _ \/ / / / / ___/ //_/
 / ___ |/ /_/ /  __/ __/ / /  /  __/  __/ / /_/ / /_/ / / / / /_/ / /_/  __/ /_/ / (__  ) ,<   
/_/  |_/ .___/\___/_/   /_/   \___/\___(_)_____/\____/_/ /_/\__,_/\__/\___/_____/_/____/_/|_|  
      /_/                                                             DonateDisk (Master Node) 
 
            ");
            Console.ResetColor();
        }

        private static DiskMasterOptions? ParseArguments(string[] args)
        {
            if (args.Length == 0) return null;

            string host = "0.0.0.0";
            int port = 9821;
            char? letter = 'Z';
            bool removable = true;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if ((arg == "-i" || arg == "--ip") && i + 1 < args.Length)
                {
                    host = args[++i];
                }
                else if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                {
                    port = p;
                    i++;
                }
                else if ((arg == "-d" || arg == "--drive") && i + 1 < args.Length)
                {
                    var str = args[++i].Trim().ToUpperInvariant();
                    if (str.Length > 0 && char.IsLetter(str[0]))
                    {
                        letter = str[0];
                    }
                }
                else if (arg == "--fixed")
                {
                    removable = false;
                }
                else if (arg == "--help" || arg == "-?")
                {
                    PrintHelp();
                    Environment.Exit(0);
                }
            }

            return new DiskMasterOptions
            {
                ListenHost = host,
                ListenPort = port,
                PreferredDriveLetter = letter,
                MountAsRemovable = removable
            };
        }

        private static DiskMasterOptions? PromptInteractiveOptions()
        {
            Console.WriteLine("检测到未指定启动参数，进入交互式引导模式：");
            Console.WriteLine();

            Console.Write("请输入本地监听端口号 (默认 9821): ");
            var portInput = Console.ReadLine()?.Trim();
            int port = 9821;
            if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out var p))
            {
                port = p;
            }

            Console.Write("请输入监听绑定的 IP 地址 (默认 0.0.0.0): ");
            var ipInput = Console.ReadLine()?.Trim();
            var host = string.IsNullOrWhiteSpace(ipInput) ? "0.0.0.0" : ipInput;

            Console.Write("请输入首选分配的驱动器盘符 (默认 Z): ");
            var driveInput = Console.ReadLine()?.Trim().ToUpperInvariant();
            char? letter = 'Z';
            if (!string.IsNullOrWhiteSpace(driveInput) && char.IsLetter(driveInput[0]))
            {
                letter = driveInput[0];
            }

            return new DiskMasterOptions
            {
                ListenHost = host,
                ListenPort = port,
                PreferredDriveLetter = letter,
                MountAsRemovable = true
            };
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
用法:
  ApeFree.DonateDisk.WinMaster [选项]

选项:
  -i, --ip <IP地址>        本地监听网络地址（默认 0.0.0.0）
  -p, --port <端口>        本地监听端口（默认 9821）
  -d, --drive <盘符>       首选挂载盘符（默认 Z，若被占用自动向下顺延）
  --fixed                  挂载为本地固定硬盘（默认挂载为可移动磁盘 RemovableDrive）
  -?, --help               显示本帮助信息

示例:
  ApeFree.DonateDisk.WinMaster -p 9821 -d Z
  ApeFree.DonateDisk.WinMaster -i 127.0.0.1 -p 8899
");
        }
    }
}
