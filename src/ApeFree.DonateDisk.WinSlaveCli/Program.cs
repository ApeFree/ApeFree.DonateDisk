using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Slave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.WinSlaveCli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var appVersion = GetAppVersion();
            Console.Title = $"ApeFree.DonateDisk.WinSlaveCli v{appVersion} - 远程虚拟磁盘分享客户端";

            var isP2P = args.Any(a => a.Equals("-p2p", StringComparison.OrdinalIgnoreCase) || a.Equals("--p2p", StringComparison.OrdinalIgnoreCase));
            return await RunAsync(args, appVersion, isP2P);
        }

        /// <summary>
        /// Slave 客户端执行入口，供本程序与 WinCli 综合工具共用
        /// </summary>
        public static async Task<int> RunAsync(string[] args, string? appVersion = null, bool isP2P = false)
        {
            appVersion ??= GetAppVersion();

            // 1. 检查是否请求帮助信息
            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-?", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
            {
                // 如果不仅传了 -h 而且后面紧跟不是参数值，判定为帮助
                bool isHelp = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].Equals("--help", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-?", StringComparison.OrdinalIgnoreCase))
                    {
                        isHelp = true;
                        break;
                    }
                    if (args[i].Equals("-h", StringComparison.OrdinalIgnoreCase) && (i + 1 >= args.Length || args[i + 1].StartsWith("-")))
                    {
                        isHelp = true;
                        break;
                    }
                }
                if (isHelp)
                {
                    PrintSlaveHelp(appVersion);
                    return 0;
                }
            }

            PrintSlaveBanner(appVersion);

            var options = ParseSlaveArguments(args, isP2P);
            if (options == null)
            {
                options = PromptInteractiveSlaveOptions(isP2P);
                if (options == null)
                {
                    return 1;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== Slave 分享客户端配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  程序版本:        v{appVersion}");
            Console.WriteLine($"  运行模式:        Slave (客户端分享节点)");
            Console.WriteLine($"  通信协议:        {(options.UseP2P ? "P2P (Libp2p 去中心化点对点)" : "标准 TCP 直连")}");
            if (options.UseP2P)
            {
                Console.WriteLine($"  P2P 服务端:      {options.P2PServerAddress ?? "未指定"}");
                if (!string.IsNullOrEmpty(options.TargetPeerId)) Console.WriteLine($"  目标 Master PeerId: {options.TargetPeerId}");
                if (!string.IsNullOrEmpty(options.P2PRemoteAddress)) Console.WriteLine($"  目标 Multiaddr:  {options.P2PRemoteAddress}");
                Console.WriteLine($"  Relay 中继穿透:  {(options.EnableRelay ? "已启用" : "未启用")}");
                if (!string.IsNullOrEmpty(options.IdentitySeed)) Console.WriteLine("  固定身份种子:    已配置");
                if (options.EnableDht) Console.WriteLine("  DHT 节点发现:    已启用");
                if (options.EnableMdns) Console.WriteLine("  mDNS 局域网发现: 已启用");
                if (options.EnableQuic) Console.WriteLine("  QUIC 传输协议:   已启用");
            }
            else
            {
                Console.WriteLine($"  Master 主机:     {options.TargetHost}:{options.TargetPort}");
            }
            Console.WriteLine($"  分享物理目录:    {options.LocalDirectory}");
            Console.WriteLine($"  卷标显示名称:    {options.VolumeLabel ?? Path.GetFileName(options.LocalDirectory.TrimEnd('\\', '/'))}");
            Console.WriteLine($"  只读保护模式:    {(options.IsReadOnly ? "已开启 [只读]" : "已关闭 [可读写]")}");
            Console.WriteLine("============================================================");
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [已连接] 成功连入 Master 端并就绪发布磁盘服务！");
                Console.ResetColor();
            };

            slaveNode.Disconnected += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [已断开] 与 Master 端的网络连接已中断，本地句柄已自动清理。");
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

            var connectTarget = options.UseP2P
                ? (!string.IsNullOrEmpty(options.TargetPeerId)
                    ? $"P2P 服务端 {options.P2PServerAddress} -> Master ({options.TargetPeerId})"
                    : (!string.IsNullOrEmpty(options.P2PRemoteAddress) ? options.P2PRemoteAddress : options.P2PServerAddress ?? "P2P 网络"))
                : $"{options.TargetHost}:{options.TargetPort}";

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在连接 Master ({connectTarget})...");

            try
            {
                var success = await slaveNode.StartAsync(cts.Token);
                if (!success)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    var errDetail = slaveNode.LastConnectResult?.Exception != null
                        ? $" 原因: {slaveNode.LastConnectResult.Exception.Message}"
                        : (slaveNode.LastConnectResult != null ? $" 错误码: {slaveNode.LastConnectResult.ErrorCode}" : string.Empty);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [等待连接] 暂未连上 Master 端 ({connectTarget}){errDetail}，已启动自动重连机制 (每 3 秒无限重试，按 Ctrl+C 可停止)...");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 磁盘捐赠已启动！按 Ctrl+C 可停止分享并安全退出。");
                    Console.ResetColor();
                }

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

        public static DiskSlaveOptions? ParseSlaveArguments(string[] args, bool isP2P)
        {
            if (args.Length == 0 && !isP2P) return null;

            string host = "127.0.0.1";
            int port = 9821;
            string? dir = null;
            string? label = null;
            bool isReadOnly = false;

            // P2P 专用选项
            bool useP2P = isP2P;
            string? p2pServerAddr = null;
            string? serverPeerId = null;
            string? serverSeed = null;
            string? targetPeerId = null;
            string? masterSeed = null;
            string? p2pRemoteAddr = null;
            string? identitySeed = null;
            int p2pPort = 0;
            bool enableRelay = false;
            bool enableDht = false;
            bool enableMdns = false;
            bool enableQuic = false;
            var bootstrapPeers = new List<string>();

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
                else if (arg == "-p2p" || arg == "--p2p")
                {
                    useP2P = true;
                }
                else if ((arg == "--server" || arg == "--p2p-server") && i + 1 < args.Length)
                {
                    p2pServerAddr = args[++i];
                    useP2P = true;
                }
                else if (arg == "--server-peer" && i + 1 < args.Length)
                {
                    serverPeerId = args[++i];
                    useP2P = true;
                }
                else if (arg == "--server-seed" && i + 1 < args.Length)
                {
                    serverSeed = args[++i];
                    useP2P = true;
                }
                else if ((arg == "--target-peer" || arg == "--master-peer" || arg == "-peer" || arg == "--peer-id") && i + 1 < args.Length)
                {
                    targetPeerId = args[++i];
                    useP2P = true;
                }
                else if ((arg == "--master-seed" || arg == "--target-seed") && i + 1 < args.Length)
                {
                    masterSeed = args[++i];
                    useP2P = true;
                }
                else if (arg == "--seed" && i + 1 < args.Length)
                {
                    identitySeed = args[++i];
                    useP2P = true;
                }
                else if ((arg == "-addr" || arg == "--remote-addr") && i + 1 < args.Length)
                {
                    p2pRemoteAddr = args[++i];
                    useP2P = true;
                }
                else if (arg == "--p2p-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pPort))
                {
                    p2pPort = pPort;
                    useP2P = true;
                    i++;
                }
                else if (arg == "--relay")
                {
                    enableRelay = true;
                }
                else if (arg == "--no-relay")
                {
                    enableRelay = false;
                }
                else if (arg == "--dht")
                {
                    enableDht = true;
                }
                else if (arg == "--mdns")
                {
                    enableMdns = true;
                }
                else if (arg == "--quic")
                {
                    enableQuic = true;
                }
                else if (arg == "--bootstrap" && i + 1 < args.Length)
                {
                    bootstrapPeers.Add(args[++i]);
                }
                else if (arg == "--help" || arg == "-?")
                {
                    PrintSlaveHelp(GetAppVersion());
                    Environment.Exit(0);
                }
            }

            if (string.IsNullOrWhiteSpace(dir))
            {
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
                IsReadOnly = isReadOnly,
                UseP2P = useP2P,
                P2PServerAddress = p2pServerAddr,
                ServerPeerId = serverPeerId,
                ServerSeed = serverSeed,
                TargetPeerId = targetPeerId,
                MasterSeed = masterSeed,
                P2PRemoteAddress = p2pRemoteAddr,
                IdentitySeed = identitySeed,
                P2PPort = p2pPort,
                EnableRelay = enableRelay,
                EnableDht = enableDht,
                EnableMdns = enableMdns,
                EnableQuic = enableQuic,
                BootstrapPeers = bootstrapPeers
            };
        }

        public static DiskSlaveOptions? PromptInteractiveSlaveOptions(bool isP2P)
        {
            Console.WriteLine("检测到未指定 Slave 详细启动参数，进入交互式引导输入：");
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

            bool useP2P = isP2P;
            if (!useP2P)
            {
                Console.Write("请选择网络传输协议 [1: TCP直连, 2: P2P点对点] (默认 1): ");
                var protoInput = Console.ReadLine()?.Trim();
                if (protoInput == "2") useP2P = true;
            }

            string host = "127.0.0.1";
            int port = 9821;
            string? p2pServerAddr = null;
            string? targetPeerId = null;
            string? identitySeed = null;

            if (useP2P)
            {
                Console.Write("请输入 P2P 服务端地址 (例如 127.0.0.1:9831 或 Multiaddr): ");
                var p2pAddrInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(p2pAddrInput)) p2pServerAddr = p2pAddrInput;

                Console.Write("请输入目标 Master 的 PeerId (选填，留空则使用默认配置): ");
                var peerInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(peerInput)) targetPeerId = peerInput;

                Console.Write("请输入本地身份私钥种子 (选填，保持 PeerId 跨重启固定): ");
                var seedInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(seedInput)) identitySeed = seedInput;
            }
            else
            {
                Console.Write("请输入 Master 端主机 IP 或域名 (默认 127.0.0.1): ");
                var hostInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(hostInput)) host = hostInput;

                Console.Write("请输入 Master 端服务端口 (默认 9821): ");
                var portInput = Console.ReadLine()?.Trim();
                if (int.TryParse(portInput, out var p)) port = p;
            }

            Console.Write("请输入挂载后显示的虚拟磁盘卷标名称 (留空使用目录名): ");
            var label = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = null;

            Console.Write("是否开启只读保护？(禁止远端修改和删除本地文件) [y/N]: ");
            var roInput = Console.ReadLine()?.Trim().ToLowerInvariant();
            bool isReadOnly = roInput == "y" || roInput == "yes";

            var fullDir = Path.GetFullPath(dir);
            return new DiskSlaveOptions
            {
                TargetHost = host,
                TargetPort = port,
                LocalDirectory = fullDir,
                VolumeLabel = label,
                IsReadOnly = isReadOnly,
                UseP2P = useP2P,
                P2PServerAddress = p2pServerAddr,
                TargetPeerId = targetPeerId,
                IdentitySeed = identitySeed,
                EnableRelay = true,
                EnableDht = false,
                EnableMdns = false,
                EnableQuic = false
            };
        }

        public static void PrintSlaveBanner(string version)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(@"
    ___               ______                 ____                    __       ____  _      __  
   /   |  ____  ___  / ____/_______  ___    / __ \____  ____  ____ _/ /____  / __ \(_)____/ /__
  / /| | / __ \/ _ \/ /_  / ___/ _ \/ _ \  / / / / __ \/ __ \/ __ `/ __/ _ \/ / / / / ___/ //_/
 / ___ |/ /_/ /  __/ __/ / /  /  __/  __/ / /_/ / /_/ / / / / /_/ / /_/  __/ /_/ / (__  ) ,<   
/_/  |_/ .___/\___/_/   /_/   \___/\___(_)_____/\____/_/ /_/\__,_/\__/\___/_____/_/____/_/|_|  
      /_/                                                             DonateDisk (Slave Node) 
            ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinSlaveCli - Slave 客户端模式 (v{version}) <<\n");
            Console.ResetColor();
        }

        public static void PrintSlaveHelp(string version)
        {
            Console.WriteLine($@"
ApeFree.DonateDisk.WinSlaveCli (v{version}) - 远程虚拟磁盘分享客户端独立工具

用法:
  ApeFree.DonateDisk.WinSlaveCli [Slave选项]     以 Slave 客户端分享模式运行
  ApeFree.DonateDisk.WinSlaveCli                 交互模式引导输入与启动

Slave 客户端分享模式选项:
  -h, --host <IP/域名>      Master 端主机 IP 地址（默认 127.0.0.1，TCP 直连模式）
  -p, --port <端口>         Master 端服务端口（默认 9821，TCP 直连模式）
  -d, --dir <本地路径>      待分享给 Master 的本地物理文件夹路径（必需）
  -n, --name <卷标名称>     挂载后显示的虚拟磁盘名称（默认使用目录名）
  -r, --readonly            以只读保护模式分享（禁止远端修改和删除文件）

P2P 专用选项:
  -p2p, --p2p               启用 P2P (Libp2p) 点对点传输模式
  --server, --p2p-server <地址> P2P 服务端地址（例如 127.0.0.1:9831 或 Multiaddr）
  --server-peer <PeerId>    指定 P2P 服务端的 PeerId（可选）
  --server-seed <种子>      指定 P2P 服务端的凭据种子（可选）
  --target-peer <PeerId>    目标 Master 端的 PeerId（通过 P2P 服务端中继连接）
  --master-seed <种子>      目标 Master 端的种子（自动计算 Master 的 PeerId）
  --seed <种子>             本地身份私钥种子（保持 PeerId 跨重启固定）
  -addr, --remote-addr <地址> 目标 Master 的完整 Multiaddr 地址
  --relay / --no-relay      是否启用 Relay 中继穿透（默认启用）
  --dht                     启用 Kademlia DHT 发现
  --mdns                    启用 mDNS 局域网发现
  --quic                    启用 QUIC 传输
  --bootstrap <Multiaddr>   添加引导节点
  -?, --help                显示本帮助信息

示例:
  # 标准 TCP 模式
  ApeFree.DonateDisk.WinSlaveCli -h 127.0.0.1 -p 9821 -d ""C:\Test"" -n ""Test""

  # P2P 模式（建议写法：连接公网 P2P 服务端，使用内置种子）
  ApeFree.DonateDisk.WinSlaveCli -p2p --server ""127.0.0.1:9831"" -d ""C:\Test""
  # P2P 模式（可选写法：指定自定义私有凭据种子）
  ApeFree.DonateDisk.WinSlaveCli -p2p --server ""127.0.0.1:9831"" --seed ""MySecretKey"" -d ""C:\Test""
");
        }

        public static string GetAppVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.1.0.0";
        }
    }
}
