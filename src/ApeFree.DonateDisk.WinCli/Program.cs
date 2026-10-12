using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using ApeFree.DonateDisk.WinSlaveCli;
using Fsp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.WinCli
{
    internal class Program
    {
        private enum RunMode
        {
            None,
            Master,
            Slave,
            P2PServer
        }

        private static async Task<int> Main(string[] args)
        {
            var appVersion = GetAppVersion();
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - 远程虚拟磁盘工具";

            // 1. 检查是否请求帮助信息
            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-?", StringComparison.OrdinalIgnoreCase) || (a.Equals("-h", StringComparison.OrdinalIgnoreCase) && !args.Any(x => x.Equals("-s", StringComparison.OrdinalIgnoreCase) || x.Equals("--slave", StringComparison.OrdinalIgnoreCase)))))
            {
                PrintOverallHelp();
                return 0;
            }

            // 2. 检测运行模式（Master、Slave 或 P2P 服务端）与 P2P 标志
            var isP2P = args.Any(a => a.Equals("-p2p", StringComparison.OrdinalIgnoreCase) || a.Equals("--p2p", StringComparison.OrdinalIgnoreCase));
            var mode = DetectMode(args, out var filteredArgs);

            if (mode == RunMode.None)
            {
                PrintMainBanner(appVersion);
                mode = PromptInteractiveMode();
                if (mode == RunMode.None)
                {
                    return 1;
                }
            }

            // 3. 根据模式分派执行
            return mode switch
            {
                RunMode.Master => await RunMasterAsync(filteredArgs, appVersion, isP2P),
                RunMode.Slave => await RunSlaveAsync(filteredArgs, appVersion, isP2P),
                RunMode.P2PServer => await RunP2PServerAsync(filteredArgs, appVersion),
                _ => 1
            };
        }

        #region 模式检测与分派

        private static RunMode DetectMode(string[] args, out string[] remainingArgs)
        {
            var argList = args.ToList();
            RunMode mode = RunMode.None;

            for (int i = 0; i < argList.Count; i++)
            {
                var arg = argList[i].ToLowerInvariant();
                if (arg == "-m" || arg == "--master")
                {
                    mode = RunMode.Master;
                    argList.RemoveAt(i);
                    break;
                }
                else if (arg == "-s" || arg == "--slave")
                {
                    mode = RunMode.Slave;
                    argList.RemoveAt(i);
                    break;
                }
                else if (arg == "--p2p-server" || arg == "-server" || arg == "--relay-server")
                {
                    mode = RunMode.P2PServer;
                    argList.RemoveAt(i);
                    break;
                }
            }

            remainingArgs = argList.ToArray();
            return mode;
        }

        private static RunMode PromptInteractiveMode()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== 请选择运行模式 ===================");
            Console.ResetColor();
            Console.WriteLine("  [1] Master 挂载服务端 - 监听网络或连接 P2P 服务端并自动挂载远程磁盘");
            Console.WriteLine("  [2] Slave  分享客户端 - 主动将本地目录分享给 Master 挂载使用");
            Console.WriteLine("  [3] P2P    引导中继端 - 启动独立公网 P2P 引导与中继服务 (Relay Server)");
            Console.WriteLine("======================================================");
            Console.Write("请输入选项 [1/2/3] (默认 1): ");
            var input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            if (string.IsNullOrEmpty(input) || input == "1")
            {
                return RunMode.Master;
            }
            if (input == "2")
            {
                return RunMode.Slave;
            }
            if (input == "3")
            {
                return RunMode.P2PServer;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("无效的选项，程序已退出。");
            Console.ResetColor();
            return RunMode.None;
        }

        #endregion

        #region Master 运行逻辑

        private static async Task<int> RunMasterAsync(string[] args, string appVersion, bool isP2P)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [Master 模式] 远程磁盘挂载服务端";
            PrintMasterBanner(appVersion);

            // 检查本地 WinFsp 驱动与运行时环境
            CheckWinFspDriver();

            var options = ParseMasterArguments(args, isP2P);
            if (options == null)
            {
                options = PromptInteractiveMasterOptions(isP2P);
                if (options == null)
                {
                    return 1;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== Master 服务端配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  程序版本:        v{appVersion}");
            Console.WriteLine($"  运行模式:        Master (服务端挂载节点)");
            Console.WriteLine($"  通信协议:        {(options.UseP2P ? "P2P (Libp2p 去中心化点对点)" : "标准 TCP 直连")}");
            if (options.UseP2P)
            {
                Console.WriteLine($"  P2P 服务端:      {(!string.IsNullOrEmpty(options.P2PServerAddress) ? options.P2PServerAddress : "未指定 (仅本地监听)")}");
                Console.WriteLine($"  Relay 中继穿透:  {(options.EnableRelay ? "已启用" : "未启用")}");
                if (!string.IsNullOrEmpty(options.IdentitySeed)) Console.WriteLine("  固定身份种子:    已配置");
                if (options.P2PPort > 0) Console.WriteLine($"  本地 P2P 端口:   {options.P2PPort}");
                if (options.EnableDht) Console.WriteLine("  DHT 节点发现:    已启用");
                if (options.EnableMdns) Console.WriteLine("  mDNS 局域网发现: 已启用");
                if (options.EnableQuic) Console.WriteLine("  QUIC 传输协议:   已启用");
            }
            else
            {
                Console.WriteLine($"  监听网络地址:    {options.ListenHost}:{options.ListenPort}");
            }
            Console.WriteLine($"  首选驱动器盘符:  {options.PreferredDriveLetter}:\\ (向下自动顺延)");
            Console.WriteLine($"  挂载磁盘类型:    {(options.MountAsRemovable ? "可移动驱动器 (Removable)" : "本地固定磁盘 (Fixed)")}");
            Console.WriteLine("=========================================================");
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

            var listenDesc = options.UseP2P
                ? (!string.IsNullOrEmpty(options.P2PServerAddress) ? $"已连接 P2P 服务端 {options.P2PServerAddress}" : "本地 P2P 网络")
                : $"TCP {options.ListenHost}:{options.ListenPort}";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 Master 服务端 ({listenDesc})...");

            try
            {
                await masterNode.StartAsync();

                if (options.UseP2P)
                {
                    if (!string.IsNullOrEmpty(options.P2PServerAddress) && !masterNode.IsListening)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        var errDetail = masterNode.LastConnectResult?.Exception != null
                            ? $" 原因: {masterNode.LastConnectResult.Exception.Message}"
                            : (masterNode.LastConnectResult != null ? $" 错误码: {masterNode.LastConnectResult.ErrorCode}" : string.Empty);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [等待连接] 暂未连上 P2P 服务端 ({options.P2PServerAddress}){errDetail}，已启动自动重连机制 (每 3 秒无限重试)...");
                        Console.ResetColor();
                    }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (!string.IsNullOrEmpty(masterNode.P2PLocalPeerId))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 就绪] Master 本地 PeerId: {masterNode.P2PLocalPeerId}");
                        if (!string.IsNullOrEmpty(options.P2PServerAddress))
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 提示] Slave 端启动参数参考:");
                            Console.WriteLine($"           ApeFree.DonateDisk.WinCli -s -p2p --server \"{options.P2PServerAddress}\" --target-peer \"{masterNode.P2PLocalPeerId}\" -d <本地目录>");
                            if (!string.IsNullOrEmpty(options.IdentitySeed))
                            {
                                Console.WriteLine($"           或 (基于种子): ApeFree.DonateDisk.WinCli -s -p2p --server \"{options.P2PServerAddress}\" --master-seed \"{options.IdentitySeed}\" -d <本地目录>");
                            }
                        }
                    }
                    if (masterNode.P2PListenAddresses?.Count > 0)
                    {
                        foreach (var addr in masterNode.P2PListenAddresses)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 监听] {addr}");
                        }
                    }
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Master 服务端正在运行，等待 Slave 连入捐赠磁盘... (按 Ctrl+C 可停止服务)");
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

        private static DiskMasterOptions? ParseMasterArguments(string[] args, bool isP2P)
        {
            if (args.Length == 0 && !isP2P) return null;

            string host = "0.0.0.0";
            int port = 9821;
            char? letter = 'Z';
            bool removable = true;

            // P2P 选项
            bool useP2P = isP2P;
            string? p2pServerAddr = null;
            string? identitySeed = null;
            string? serverPeerId = null;
            string? serverSeed = null;
            string? targetPeerId = null;
            int p2pPort = 0;
            bool enableRelay = false;
            bool enableDht = false;
            bool enableMdns = false;
            bool enableQuic = false;
            var bootstrapPeers = new List<string>();

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
                else if (arg == "--seed" && i + 1 < args.Length)
                {
                    identitySeed = args[++i];
                    useP2P = true;
                }
                else if ((arg == "--target-peer" || arg == "--slave-peer") && i + 1 < args.Length)
                {
                    targetPeerId = args[++i];
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
                    PrintMasterHelp();
                    Environment.Exit(0);
                }
            }

            return new DiskMasterOptions
            {
                ListenHost = host,
                ListenPort = port,
                PreferredDriveLetter = letter,
                MountAsRemovable = removable,
                UseP2P = useP2P,
                P2PServerAddress = p2pServerAddr,
                ServerPeerId = serverPeerId,
                ServerSeed = serverSeed,
                IdentitySeed = identitySeed,
                TargetPeerId = targetPeerId,
                P2PPort = p2pPort,
                EnableRelay = enableRelay,
                EnableDht = enableDht,
                EnableMdns = enableMdns,
                EnableQuic = enableQuic,
                BootstrapPeers = bootstrapPeers
            };
        }

        private static DiskMasterOptions? PromptInteractiveMasterOptions(bool isP2P)
        {
            Console.WriteLine("检测到未指定 Master 详细启动参数，进入交互式引导配置：");
            Console.WriteLine();

            bool useP2P = isP2P;
            if (!useP2P)
            {
                Console.Write("请选择网络传输协议 [1: TCP直连, 2: P2P点对点] (默认 1): ");
                var protoInput = Console.ReadLine()?.Trim();
                if (protoInput == "2") useP2P = true;
            }

            int port = 9821;
            string host = "0.0.0.0";
            string? p2pServerAddr = null;
            string? identitySeed = null;

            if (useP2P)
            {
                Console.Write("请输入 P2P 服务端地址 (例如 127.0.0.1:9831，留空仅在本地网络运行): ");
                var sInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(sInput))
                {
                    p2pServerAddr = sInput;
                }

                Console.Write("请输入固定身份私钥种子 (留空自动生成随机身份): ");
                var seedInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(seedInput))
                {
                    identitySeed = seedInput;
                }
            }
            else
            {
                Console.Write("请输入本地监听端口号 (默认 9821): ");
                var portInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out var p))
                {
                    port = p;
                }

                Console.Write("请输入监听绑定的 IP 地址 (默认 0.0.0.0): ");
                var ipInput = Console.ReadLine()?.Trim();
                host = string.IsNullOrWhiteSpace(ipInput) ? "0.0.0.0" : ipInput;
            }

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
                MountAsRemovable = true,
                UseP2P = useP2P,
                P2PServerAddress = p2pServerAddr,
                IdentitySeed = identitySeed,
                EnableRelay = false
            };
        }

        private static void CheckWinFspDriver()
        {
            try
            {
                var fs = new FileSystemBase();
                using var host = new FileSystemHost(fs);
                host.Preflight("Z:");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[环境自检] 已检测到 WinFsp 内核驱动与运行时就绪。");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[环境警告] 检测到当前系统可能未正确安装 WinFsp 驱动运行时：");
                Console.WriteLine($"           详情: {ex.Message}");
                Console.WriteLine("           若 Slave 连接后挂载失败，请先安装 WinFsp 运行时：");
                Console.WriteLine("           下载地址: https://github.com/winfsp/winfsp/releases 或执行 `winget install WinFsp.WinFsp`");
                Console.WriteLine();
                Console.ResetColor();
            }
        }

        #endregion

        #region Slave 运行逻辑

        private static async Task<int> RunSlaveAsync(string[] args, string appVersion, bool isP2P)
        {
            return await ApeFree.DonateDisk.WinSlaveCli.Program.RunAsync(args, appVersion, isP2P);
        }

        #endregion

        #region P2P 服务端运行逻辑

        private static async Task<int> RunP2PServerAsync(string[] args, string appVersion)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [P2P 服务端模式] 引导与中继节点";
            PrintP2PServerBanner(appVersion);

            string host = "0.0.0.0";
            int port = 9831;
            string? seed = null;
            bool enableQuic = false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                {
                    port = p;
                    i++;
                }
                else if ((arg == "-i" || arg == "--ip" || arg == "--host") && i + 1 < args.Length)
                {
                    host = args[++i];
                }
                else if (arg == "--seed" && i + 1 < args.Length)
                {
                    seed = args[++i];
                }
                else if (arg == "--quic")
                {
                    enableQuic = true;
                }
                else if (arg == "--help" || arg == "-?")
                {
                    PrintP2PServerHelp();
                    return 0;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== P2P 服务端配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  程序版本:        v{appVersion}");
            Console.WriteLine($"  运行模式:        P2P 引导与数据中继服务端 (Bootstrap & Data Relay)");
            Console.WriteLine($"  监听绑定地址:    {host}:{port}");
            Console.WriteLine($"  服务端凭据种子:  {(!string.IsNullOrWhiteSpace(seed) ? seed : "默认预设 (可通过 --seed 指定自定义口令)")}");
            Console.WriteLine($"  全双工数据桥接:  已启用 (Transparent Byte Bridging)");
            Console.WriteLine($"  DHT 路由发现:    已启用");
            if (enableQuic) Console.WriteLine("  QUIC 传输协议:   已启用");
            Console.WriteLine("======================================================");
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[系统] 正在关闭 P2P 服务端...");
                cts.Cancel();
            };

            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = host;
            server.Options.TcpPort = port;
            server.Options.EnableRelay = false;
            server.Options.EnableDht = true;
            server.Options.EnableMdns = true;
            server.Options.EnableQuic = enableQuic;

            string effectiveSeed = !string.IsNullOrWhiteSpace(seed) ? seed : P2PAddressHelper.DefaultServerSeed;
            server.Options.IdentitySeed = P2PAddressHelper.ParseSeed(effectiveSeed);

            using var relayManager = new P2PRelayManager();

            server.PeerConnected += (s, e) =>
            {
                var c = e.Client;
                relayManager.RegisterClient(c);
                var count = relayManager.ActivePeerCount;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [节点连入] 远端 PeerId: {c.RemotePeerId ?? "未知"} 地址: {c.RemoteAddress}");
                Console.ResetColor();

                if (count >= 2)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [中继桥接] 当前在线节点数: {count}，全双工双向中继数据隧道已建立就绪！");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [中继桥接] 节点已就绪 ({c.RemotePeerId})，等待对端连入建立数据桥接 (当前在线: {count})...");
                }
            };

            server.PeerDisconnected += (s, e) =>
            {
                var c = e.Client;
                relayManager.UnregisterClient(c);
                var count = relayManager.ActivePeerCount;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [节点断开] 远端 PeerId: {c.RemotePeerId ?? "未知"} 原因: {e.DisconnectedInfo.ReasonCode} (剩余在线: {count})");
                Console.ResetColor();
            };

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 P2P 服务端并在 {host}:{port} 监听...");
                await server.StartAsync();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 服务端就绪] 本地 PeerId: {server.LocalPeerId}");
                Console.ResetColor();
                foreach (var addr in server.ListenAddresses)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [监听 Multiaddr] {addr}");
                }
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("---------------- 客户端快速连接指令参考 ----------------");
                var displayHost = (host == "0.0.0.0" || host == "127.0.0.1") ? "<公网或局域网IP>" : host;
                Console.WriteLine("【Master 端连接命令】:");
                Console.WriteLine($"  ApeFree.DonateDisk.WinCli -m -p2p --server \"{displayHost}:{port}\" -d Z");
                Console.WriteLine();
                Console.WriteLine("【Slave 端连接命令】:");
                Console.WriteLine($"  ApeFree.DonateDisk.WinCli -s -p2p --server \"{displayHost}:{port}\" --target-peer \"<Master的PeerId>\" -d <待分享目录>");
                Console.WriteLine("---------------------------------------------------------");
                Console.ResetColor();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 引导与中继服务正在运行中，按 Ctrl+C 可停止。");
                Console.ResetColor();

                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 服务端异常: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
            finally
            {
                await server.CloseAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 服务端已关闭。");
            }

            return 0;
        }

        #endregion

        #region 横幅与帮助信息

        private static string GetAppVersion()
        {
            var infoVer = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                var plusIndex = infoVer.IndexOf('+');
                return plusIndex > 0 ? infoVer.Substring(0, plusIndex) : infoVer;
            }

            var ver = typeof(Program).Assembly.GetName().Version;
            return ver != null ? ver.ToString() : "0.0.1.0";
        }

        private static void PrintMainBanner(string version)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
    ___               ______                 ____                    __       ____  _      __  
   /   |  ____  ___  / ____/_______  ___    / __ \____  ____  ____ _/ /____  / __ \(_)____/ /__
  / /| | / __ \/ _ \/ /_  / ___/ _ \/ _ \  / / / / __ \/ __ \/ __ `/ __/ _ \/ / / / / ___/ //_/
 / ___ |/ /_/ /  __/ __/ / /  /  __/  __/ / /_/ / /_/ / / / / /_/ / /_/  __/ /_/ / (__  ) ,<   
/_/  |_/ .___/\___/_/   /_/   \___/\___(_)_____/\____/_/ /_/\__,_/\__/\___/_____/_/____/_/|_|  
      /_/                                                              DonateDisk (WinCli) 
            ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli 综合命令行终端 (v{version}) <<\n");
            Console.ResetColor();
        }

        private static void PrintMasterBanner(string version)
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
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli - Master 服务端模式 (v{version}) <<\n");
            Console.ResetColor();
        }

        private static void PrintSlaveBanner(string version)
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
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli - Slave 客户端模式 (v{version}) <<\n");
            Console.ResetColor();
        }

        private static void PrintP2PServerBanner(string version)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(@"
    ___               ______                 ____                    __       ____  _      __  
   /   |  ____  ___  / ____/_______  ___    / __ \____  ____  ____ _/ /____  / __ \(_)____/ /__
  / /| | / __ \/ _ \/ /_  / ___/ _ \/ _ \  / / / / __ \/ __ \/ __ `/ __/ _ \/ / / / / ___/ //_/
 / ___ |/ /_/ /  __/ __/ / /  /  __/  __/ / /_/ / /_/ / / / / /_/ / /_/  __/ /_/ / (__  ) ,<   
/_/  |_/ .___/\___/_/   /_/   \___/\___(_)_____/\____/_/ /_/\__,_/\__/\___/_____/_/____/_/|_|  
      /_/                                                          DonateDisk (P2P Relay Server) 
            ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli - P2P 中继与引导服务端 (v{version}) <<\n");
            Console.ResetColor();
        }

        private static void PrintOverallHelp()
        {
            var version = GetAppVersion();
            Console.WriteLine($@"
ApeFree.DonateDisk.WinCli (v{version}) - 远程虚拟磁盘挂载与分享综合工具

用法:
  ApeFree.DonateDisk.WinCli -m [Master选项]     以 Master 挂载服务端模式运行
  ApeFree.DonateDisk.WinCli -s [Slave选项]      以 Slave 分享客户端模式运行
  ApeFree.DonateDisk.WinCli --p2p-server [选项] 启动独立 P2P 引导与中继服务端
  ApeFree.DonateDisk.WinCli                     交互模式引导选择与启动

--------------------------------------------------------------------------------
【Master 服务端挂载模式选项 (-m, --master)】:
  -i, --ip <IP地址>         本地监听网络地址（默认 0.0.0.0，TCP 直连模式）
  -p, --port <端口>         本地 TCP 监听端口（默认 9821，TCP 直连模式）
  -d, --drive <盘符>        首选挂载盘符（默认 Z，若被占用自动顺延）
  --fixed                   挂载为固定硬盘（默认挂载为可移动磁盘 RemovableDrive）

  [P2P 专用选项]:
  -p2p, --p2p               启用 P2P (Libp2p) 去中心化模式
  --server, --p2p-server <地址> P2P 服务端地址（例如 127.0.0.1:9831 或 Multiaddr）
  --server-peer <PeerId>    指定 P2P 服务端的 PeerId（可选）
  --server-seed <种子>      指定 P2P 服务端的凭据种子（可选）
  --seed <种子>             本地身份私钥种子（字符串或十六进制，保持 PeerId 跨重启固定）
  --target-peer <PeerId>    指定目标 Slave 端的 PeerId（可选）
  --relay / --no-relay      是否启用 Relay 中继穿透（P2P 模式下默认启用）
  --dht                     启用 Kademlia DHT 节点发现
  --mdns                    启用 mDNS 局域网节点发现
  --quic                    启用 QUIC 传输协议
  --bootstrap <Multiaddr>   额外添加引导节点地址

    示例:
    # 标准 TCP 直连模式
    ApeFree.DonateDisk.WinCli -m -p 9821 -d Z
    # P2P 模式（建议写法：连接公网 P2P 服务端，使用内置种子）
    ApeFree.DonateDisk.WinCli -m -p2p --server ""127.0.0.1:9831"" -d Z
    # P2P 模式（可选写法：指定自定义私有凭据种子）
    ApeFree.DonateDisk.WinCli -m -p2p --server ""127.0.0.1:9831"" --seed ""MySecretKey"" -d Z

--------------------------------------------------------------------------------
【Slave 客户端分享模式选项 (-s, --slave)】:
  -h, --host <IP/域名>      Master 端主机 IP 地址（默认 127.0.0.1，TCP 直连模式）
  -p, --port <端口>         Master 端服务端口（默认 9821，TCP 直连模式）
  -d, --dir <本地路径>      待分享给 Master 的本地物理文件夹路径（必需）
  -n, --name <卷标名称>     挂载后显示的虚拟磁盘名称（默认使用目录名）
  -r, --readonly            以只读保护模式分享（禁止远端修改和删除文件）

  [P2P 专用选项]:
  -p2p, --p2p               启用 P2P (Libp2p) 点对点传输模式
  --server, --p2p-server <地址> P2P 服务端地址（例如 127.0.0.1:9831 或 Multiaddr）
  --server-peer <PeerId>    指定 P2P 服务端的 PeerId（可选）
  --server-seed <种子>      指定 P2P 服务端的凭据种子（可选）
  --target-peer <PeerId>    目标 Master 端的 PeerId（通过 P2P 服务端中继寻址）
  --master-seed <种子>      目标 Master 端的种子（自动计算 Master 的 PeerId）
  --seed <种子>             本地身份私钥种子（保持 PeerId 跨重启固定）
  -addr, --remote-addr <地址> 目标远端完整的 Multiaddr 地址（若指定则优先使用）
  --relay / --no-relay      是否启用 Relay 中继穿透（P2P 模式下默认启用）
  --dht                     启用 Kademlia DHT 发现
  --mdns                    启用 mDNS 局域网发现
  --quic                    启用 QUIC 传输
  --bootstrap <Multiaddr>   额外添加引导节点地址

  示例:
    # 标准 TCP 模式
    ApeFree.DonateDisk.WinCli -s -h 127.0.0.1 -p 9821 -d ""C:\Test"" -n ""Test""

    # P2P 模式（建议写法：连接公网 P2P 服务端，使用内置种子）
    ApeFree.DonateDisk.WinCli -s -p2p --server ""127.0.0.1:9831"" -d ""C:\Test""
    # P2P 模式（可选写法：指定自定义私有凭据种子）
    ApeFree.DonateDisk.WinCli -s -p2p --server ""127.0.0.1:9831"" --seed ""MySecretKey"" -d ""C:\Test""

--------------------------------------------------------------------------------
【独立 P2P 引导与中继服务端模式 (--p2p-server)】:
  -p, --port <端口>         监听端口（默认 9831）
  -i, --ip <IP地址>         监听绑定 IP（默认 0.0.0.0）
  --seed <种子>             服务端身份私钥种子（可选，留空使用内置默认种子）
  --quic                    启用 QUIC 协议支持

  示例:
    ApeFree.DonateDisk.WinCli --p2p-server -p 9831
");
        }

        private static void PrintMasterHelp()
        {
            Console.WriteLine(@"
Master 服务端挂载模式选项:
  -i, --ip <IP地址>         本地监听网络地址（默认 0.0.0.0，TCP 直连模式）
  -p, --port <端口>         本地 TCP 监听端口（默认 9821，TCP 直连模式）
  -d, --drive <盘符>        首选挂载盘符（默认 Z，若被占用自动顺延）
  --fixed                   挂载为本地固定硬盘（默认挂载为可移动磁盘 RemovableDrive）

P2P 专用选项:
  -p2p, --p2p               启用 P2P (Libp2p) 模式
  --server, --p2p-server <地址> P2P 服务端地址（例如 127.0.0.1:9831 或 Multiaddr）
  --server-peer <PeerId>    指定 P2P 服务端的 PeerId（可选）
  --server-seed <种子>      指定 P2P 服务端的凭据种子（可选）
  --seed <种子>             本地身份私钥种子（保持 PeerId 跨重启固定）
  --target-peer <PeerId>    指定目标 Slave 端的 PeerId（可选）
  --relay / --no-relay      是否启用 Relay 中继穿透（默认启用）
  --dht                     启用 Kademlia DHT 发现
  --mdns                    启用 mDNS 局域网发现
  --quic                    启用 QUIC 传输
  --bootstrap <Multiaddr>   添加引导节点
  -?, --help                显示本帮助信息

示例:
  ApeFree.DonateDisk.WinCli -m -p 9821 -d Z
  ApeFree.DonateDisk.WinCli -m -p2p --server ""127.0.0.1:9831"" -d Z
  ApeFree.DonateDisk.WinCli -m -p2p --server ""127.0.0.1:9831"" --seed ""MySecretKey"" -d Z
");
        }

        private static void PrintSlaveHelp()
        {
            Console.WriteLine(@"
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
  ApeFree.DonateDisk.WinCli -s -h 127.0.0.1 -p 9821 -d ""C:\Test"" -n ""Test""
  ApeFree.DonateDisk.WinCli -s -p2p --server ""127.0.0.1:9831"" -d ""C:\Test""
  ApeFree.DonateDisk.WinCli -s -p2p --server ""127.0.0.1:9831"" --seed ""MySecretKey"" -d ""C:\Test""
");
        }

        private static void PrintP2PServerHelp()
        {
            Console.WriteLine(@"
独立 P2P 引导与中继服务端模式选项:
  -p, --port <端口>         监听端口（默认 9831）
  -i, --ip <IP地址>         监听绑定 IP（默认 0.0.0.0）
  --seed <种子>             服务端身份私钥种子（可选，留空使用内置默认种子）
  --quic                    启用 QUIC 协议支持
  -?, --help                显示本帮助信息

示例:
  ApeFree.DonateDisk.WinCli --p2p-server -p 9831
");
        }

        #endregion
    }
}
