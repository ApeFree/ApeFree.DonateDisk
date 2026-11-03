using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using ApeFree.DonateDisk.WinCli.Common;
using DokanNet;
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
        internal enum RunMode
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
            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-?", StringComparison.OrdinalIgnoreCase) ||
                              (a.Equals("-h", StringComparison.OrdinalIgnoreCase) && !args.Any(x => x.Equals("-s", StringComparison.OrdinalIgnoreCase) || x.Equals("--slave", StringComparison.OrdinalIgnoreCase) || x.Equals("-m", StringComparison.OrdinalIgnoreCase) || x.Equals("--master", StringComparison.OrdinalIgnoreCase)))))
            {
                PrintOverallHelp();
                return 0;
            }

            // 2. 检测运行模式与 P2P 标志
            var isP2P = args.Any(a => a.Equals("-p2p", StringComparison.OrdinalIgnoreCase) || a.Equals("--p2p", StringComparison.OrdinalIgnoreCase));
            var mode = DetectMode(args, out var filteredArgs);

            // 3. 无参或未指定模式时，启动由 ApeFree.PyramidMenu 驱动的交互式分层向导
            if (mode == RunMode.None)
            {
                var wizardResult = DonateDiskMenuWizard.Run();
                if (wizardResult == null)
                {
                    return 0;
                }

                return wizardResult.Mode switch
                {
                    WizardRunMode.Master => await RunMasterWithOptionsAsync(wizardResult.MasterOptions!, appVersion),
                    WizardRunMode.Slave => await RunSlaveWithOptionsAsync(wizardResult.SlaveOptions!, appVersion),
                    WizardRunMode.P2PServer => await RunP2PServerWithOptionsAsync(wizardResult.P2PServerPort, "0.0.0.0", null, false, appVersion),
                    _ => 0
                };
            }

            // 4. 命令行参数模式
            return mode switch
            {
                RunMode.Master => await RunMasterFromArgsAsync(filteredArgs, appVersion, isP2P),
                RunMode.Slave => await RunSlaveFromArgsAsync(filteredArgs, appVersion, isP2P),
                RunMode.P2PServer => await RunP2PServerFromArgsAsync(filteredArgs, appVersion),
                _ => 1
            };
        }

        #region 模式检测

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

        #endregion

        #region Master 运行逻辑

        private static async Task<int> RunMasterFromArgsAsync(string[] args, string appVersion, bool isP2P)
        {
            CheckDokanyDriver();

            var options = ParseMasterArguments(args, isP2P);
            if (options == null)
            {
                var wizardResult = DonateDiskMenuWizard.Run();
                if (wizardResult?.MasterOptions == null) return 1;
                options = wizardResult.MasterOptions;
            }

            return await RunMasterWithOptionsAsync(options, appVersion);
        }

        private static async Task<int> RunMasterWithOptionsAsync(DiskMasterOptions options, string appVersion)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [Master 模式] 远程磁盘挂载端";
            PrintMasterBanner(appVersion);
            CheckDokanyDriver();

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== Master 端运行配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  程序版本:        v{appVersion}");
            Console.WriteLine($"  运行身份:        Master (远程磁盘挂载端)");

            if (options.TransportMode == NetworkTransportMode.P2P)
            {
                Console.WriteLine($"  网络模式:        P2P (Libp2p 去中心化中继穿透)");
                Console.WriteLine($"  P2P 服务端:      {(!string.IsNullOrEmpty(options.P2PServerAddress) ? options.P2PServerAddress : "未指定 (仅本地监听)")}");
                Console.WriteLine($"  Relay 中继穿透:  {(options.EnableRelay ? "已启用" : "未启用")}");
                if (!string.IsNullOrEmpty(options.IdentitySeed)) Console.WriteLine("  固定身份种子:    已配置");
                if (options.P2PPort > 0) Console.WriteLine($"  本地 P2P 端口:   {options.P2PPort}");
                if (options.EnableDht) Console.WriteLine("  DHT 节点发现:    已启用");
                if (options.EnableMdns) Console.WriteLine("  mDNS 局域网发现: 已启用");
                if (options.EnableQuic) Console.WriteLine("  QUIC 传输协议:   已启用");
            }
            else if (options.TransportMode == NetworkTransportMode.Connect)
            {
                Console.WriteLine($"  网络模式:        标准 TCP 主动拨号 (正向挂载)");
                Console.WriteLine($"  目标 Slave 地址: {options.TargetHost}:{options.TargetPort}");
                Console.WriteLine($"  自动重连机制:    已启用 (每 3 秒无限自动重试)");
            }
            else
            {
                Console.WriteLine($"  网络模式:        标准 TCP 服务端监听 (反向分享挂载)");
                Console.WriteLine($"  本地监听地址:    {options.ListenHost}:{options.ListenPort}");
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
                Console.WriteLine($"           - 对端来源:   {session.Client}");
                Console.WriteLine($"           - 卷标名称:   {session.VolumeInfo.VolumeLabel}");
                Console.WriteLine($"           - 驱动器路径: 可以在 Windows 资源管理器或 CMD 中访问 {session.MountPoint}");
                Console.WriteLine();
            };

            masterNode.SlaveUnmounted += (s, session) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [磁盘已卸载] 对端断开连接，盘符 [{session.MountPoint}] 已自动安全卸载并释放。");
                Console.ResetColor();
                Console.WriteLine();
            };

            masterNode.MountFailed += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [× 挂载失败] 来自对端 '{e.Client}' 的磁盘挂载出错: {e.Exception.Message}");
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

            var listenDesc = options.TransportMode switch
            {
                NetworkTransportMode.P2P => (!string.IsNullOrEmpty(options.P2PServerAddress) ? $"已连接 P2P 服务端 {options.P2PServerAddress}" : "本地 P2P 网络"),
                NetworkTransportMode.Connect => $"TCP 主动连向 {options.TargetHost}:{options.TargetPort}",
                _ => $"TCP 本地监听 {options.ListenHost}:{options.ListenPort}"
            };
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 Master 节点 ({listenDesc})...");

            try
            {
                await masterNode.StartAsync();

                if (options.TransportMode == NetworkTransportMode.P2P)
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
                            Console.WriteLine($"           监听地址: {addr}");
                        }
                    }
                    Console.ResetColor();
                }
                else if (options.TransportMode == NetworkTransportMode.Connect)
                {
                    if (!masterNode.IsListening)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        var errDetail = masterNode.LastConnectResult?.Exception != null
                            ? $" 原因: {masterNode.LastConnectResult.Exception.Message}"
                            : string.Empty;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [等待连接] 暂未连上目标 Slave ({options.TargetHost}:{options.TargetPort}){errDetail}，已启动后台自动无限重连...");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 成功建立与目标 Slave 的 TCP 物理连接！正在挂载虚拟磁盘...");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Master 服务端监听已就绪，正在等待 Slave 客户端连入...");
                    Console.ResetColor();
                }

                Console.WriteLine("[提示] 按 Ctrl+C 可安全卸载并退出程序。");
                Console.WriteLine();

                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Master 运行异常: {ex.Message}");
                Console.ResetColor();
                return 2;
            }
            finally
            {
                await masterNode.StopAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 所有磁盘会话已安全释放，Master 节点退出。");
            }

            return 0;
        }

        private static DiskMasterOptions? ParseMasterArguments(string[] args, bool isP2P)
        {
            if (args.Length == 0 && !isP2P) return null;

            string host = "0.0.0.0";
            string? targetHost = null;
            int port = 9821;
            char? letter = 'Z';
            bool removable = true;
            bool isExplicitConnect = false;
            bool isExplicitListen = false;

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
                else if ((arg == "-h" || arg == "--host" || arg == "-target" || arg == "--target") && i + 1 < args.Length)
                {
                    targetHost = args[++i];
                    isExplicitConnect = true;
                }
                else if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                {
                    port = p;
                    i++;
                }
                else if (arg == "-l" || arg == "--listen")
                {
                    isExplicitListen = true;
                }
                else if (arg == "-c" || arg == "--connect")
                {
                    isExplicitConnect = true;
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

            var transportMode = NetworkTransportMode.Listen;
            if (useP2P)
            {
                transportMode = NetworkTransportMode.P2P;
            }
            else if (isExplicitConnect || (!string.IsNullOrEmpty(targetHost) && !isExplicitListen))
            {
                transportMode = NetworkTransportMode.Connect;
            }

            return new DiskMasterOptions
            {
                TransportMode = transportMode,
                TargetHost = targetHost ?? "127.0.0.1",
                TargetPort = port,
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

        #endregion

        #region Slave 运行逻辑

        private static async Task<int> RunSlaveFromArgsAsync(string[] args, string appVersion, bool isP2P)
        {
            var options = ParseSlaveArguments(args, isP2P);
            if (options == null)
            {
                var wizardResult = DonateDiskMenuWizard.Run();
                if (wizardResult?.SlaveOptions == null) return 1;
                options = wizardResult.SlaveOptions;
            }

            return await RunSlaveWithOptionsAsync(options, appVersion);
        }

        private static async Task<int> RunSlaveWithOptionsAsync(DiskSlaveOptions options, string appVersion)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [Slave 模式] 本地磁盘分享端";
            PrintSlaveBanner(appVersion);

            if (string.IsNullOrWhiteSpace(options.LocalDirectory) || !Directory.Exists(options.LocalDirectory))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[错误] 指定待分享的本地目录不存在或无法访问: '{options.LocalDirectory}'");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== Slave 端运行配置 ===================");
            Console.ResetColor();
            Console.WriteLine($"  程序版本:        v{appVersion}");
            Console.WriteLine($"  运行身份:        Slave (本地目录分享端)");
            Console.WriteLine($"  待分享本地目录:  {options.LocalDirectory}");
            Console.WriteLine($"  虚拟磁盘卷标:    {(string.IsNullOrEmpty(options.VolumeLabel) ? "(自动取目录名)" : options.VolumeLabel)}");
            Console.WriteLine($"  只读保护模式:    {(options.IsReadOnly ? "是 (只读保护)" : "否 (允许读写)")}");

            if (options.TransportMode == NetworkTransportMode.P2P)
            {
                Console.WriteLine($"  网络模式:        P2P (Libp2p 去中心化中继穿透)");
                Console.WriteLine($"  P2P 服务端:      {(!string.IsNullOrEmpty(options.P2PServerAddress) ? options.P2PServerAddress : "未指定")}");
                if (!string.IsNullOrEmpty(options.TargetPeerId)) Console.WriteLine($"  目标 Master PeerId: {options.TargetPeerId}");
                if (!string.IsNullOrEmpty(options.MasterSeed)) Console.WriteLine($"  目标 Master 凭据:   已配置");
                if (!string.IsNullOrEmpty(options.IdentitySeed)) Console.WriteLine("  固定身份种子:    已配置");
                if (options.EnableDht) Console.WriteLine("  DHT 节点发现:    已启用");
                if (options.EnableMdns) Console.WriteLine("  mDNS 局域网发现: 已启用");
                if (options.EnableQuic) Console.WriteLine("  QUIC 传输协议:   已启用");
            }
            else if (options.TransportMode == NetworkTransportMode.Listen)
            {
                Console.WriteLine($"  网络模式:        标准 TCP 本地服务监听 (正向被控模式)");
                Console.WriteLine($"  本地监听地址:    {options.ListenHost}:{options.ListenPort}");
                Console.WriteLine($"  多端并发接入:    完全允许 (基于客户端连接隔离句柄生命周期)");
            }
            else
            {
                Console.WriteLine($"  网络模式:        标准 TCP 主动拨号连出 (反向穿透模式)");
                Console.WriteLine($"  目标 Master 地址: {options.TargetHost}:{options.TargetPort}");
                Console.WriteLine($"  自动重连机制:    已启用 (每 3 秒无限自动重试)");
            }
            Console.WriteLine("=========================================================");
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[系统] 正在关闭 Slave 服务并释放所有文件流...");
                cts.Cancel();
            };

            using var slaveNode = new DiskSlaveNode(options);

            slaveNode.Connected += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [连接成功] 成功连入 Master 端！磁盘服务已发布。");
                Console.ResetColor();
            };

            slaveNode.Disconnected += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [连接断开] 与 Master 端的连接已中断，本地流资源已安全释放，后台自动无限重连中...");
                Console.ResetColor();
            };

            slaveNode.ClientConnected += (s, client) =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Master 接入] 远端 Master 客户端已接入: {client} (当前在线 Master 数: {slaveNode.ConnectedClientCount})");
                Console.ResetColor();
            };

            slaveNode.ClientDisconnected += (s, client) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Master 离线] 远端 Master 客户端已断开: {client} (剩余在线 Master 数: {slaveNode.ConnectedClientCount})");
                Console.ResetColor();
            };

            slaveNode.FileChanged += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var detail = string.IsNullOrEmpty(e.OldRelativePath)
                    ? e.RelativePath
                    : $"{e.OldRelativePath} -> {e.RelativePath}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [本地变动] [{e.ChangeType}] {detail}");
                Console.ResetColor();
            };

            var connectDesc = options.TransportMode switch
            {
                NetworkTransportMode.P2P => (!string.IsNullOrEmpty(options.P2PServerAddress) ? $"P2P 连往 {options.P2PServerAddress}" : "P2P 本地"),
                NetworkTransportMode.Listen => $"TCP 本地监听 {options.ListenHost}:{options.ListenPort}",
                _ => $"TCP 连往 {options.TargetHost}:{options.TargetPort}"
            };
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 Slave 节点 ({connectDesc})...");

            try
            {
                var started = await slaveNode.StartAsync(cts.Token);
                if (!started && options.TransportMode != NetworkTransportMode.Listen)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    var errDetail = slaveNode.LastConnectResult?.Exception != null
                        ? $" 原因: {slaveNode.LastConnectResult.Exception.Message}"
                        : string.Empty;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [初次连接未成功]{errDetail}，已启动后台持续重连机制 (每 3 秒无限重试)...");
                    Console.ResetColor();
                }
                else if (options.TransportMode == NetworkTransportMode.Listen)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Slave 服务端监听已启动，等待 Master 连入挂载...");
                    Console.ResetColor();
                }

                if (options.TransportMode == NetworkTransportMode.P2P && !string.IsNullOrEmpty(slaveNode.LocalPeerId))
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 就绪] Slave 本地 PeerId: {slaveNode.LocalPeerId}");
                    Console.ResetColor();
                }

                Console.WriteLine("[提示] 按 Ctrl+C 可安全停止分享并退出程序。");
                Console.WriteLine();

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

        private static DiskSlaveOptions? ParseSlaveArguments(string[] args, bool isP2P)
        {
            if (args.Length == 0 && !isP2P) return null;

            string? host = null;
            int port = 9821;
            string? dir = null;
            string? label = null;
            bool isReadOnly = false;
            bool isExplicitConnect = false;
            bool isExplicitListen = false;

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
                if ((arg == "-h" || arg == "--host" || arg == "-target" || arg == "--target") && i + 1 < args.Length)
                {
                    host = args[++i];
                    isExplicitConnect = true;
                }
                else if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                {
                    port = p;
                    i++;
                }
                else if ((arg == "-i" || arg == "--ip") && i + 1 < args.Length)
                {
                    host = args[++i];
                }
                else if (arg == "-l" || arg == "--listen")
                {
                    isExplicitListen = true;
                }
                else if (arg == "-c" || arg == "--connect")
                {
                    isExplicitConnect = true;
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
                    PrintSlaveHelp();
                    Environment.Exit(0);
                }
            }

            var transportMode = NetworkTransportMode.Connect;
            if (useP2P)
            {
                transportMode = NetworkTransportMode.P2P;
            }
            else if (isExplicitListen && !isExplicitConnect)
            {
                transportMode = NetworkTransportMode.Listen;
            }

            return new DiskSlaveOptions
            {
                TransportMode = transportMode,
                TargetHost = host ?? "127.0.0.1",
                TargetPort = port,
                ListenHost = host ?? "0.0.0.0",
                ListenPort = port,
                LocalDirectory = !string.IsNullOrWhiteSpace(dir) ? Path.GetFullPath(dir) : Environment.CurrentDirectory,
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

        #endregion

        #region P2P 服务端运行逻辑

        private static async Task<int> RunP2PServerFromArgsAsync(string[] args, string appVersion)
        {
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

            return await RunP2PServerWithOptionsAsync(port, host, seed, enableQuic, appVersion);
        }

        private static async Task<int> RunP2PServerWithOptionsAsync(int port, string host, string? seed, bool enableQuic, string appVersion)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [P2P 服务端模式] 引导与中继节点";
            PrintP2PServerBanner(appVersion);

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
                Console.WriteLine($"           - 当前在线中继节点数: {count}");

                if (count >= 2)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [桥接就绪] 检测到当前已有 {count} 个节点在线，已全自动建立双向全双工字节流桥接！");
                    Console.ResetColor();
                }
            };

            server.PeerDisconnected += (s, e) =>
            {
                var c = e.Client;
                relayManager.UnregisterClient(c);
                var count = relayManager.ActivePeerCount;

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [节点断开] 远端 PeerId: {c.RemotePeerId ?? "未知"} 地址: {c.RemoteAddress} (当前在线中继节点数: {count})");
                Console.ResetColor();
            };

            try
            {
                await server.StartAsync();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 引导与中继服务启动成功！");
                Console.ResetColor();
                Console.WriteLine($"  服务端 LocalPeerId: {server.LocalPeerId}");
                Console.WriteLine($"  服务端固定种子:     {effectiveSeed}");
                Console.WriteLine();
                Console.WriteLine("客户端启动命令参考 (内置种子，双方连入相同服务端即可自动打通):");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  Master 端: ApeFree.DonateDisk.WinCli -m -p2p --server \"127.0.0.1:{port}\" -d Z");
                Console.WriteLine($"  Slave  端: ApeFree.DonateDisk.WinCli -s -p2p --server \"127.0.0.1:{port}\" -d \"C:\\Share\"");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("[提示] 按 Ctrl+C 可停止服务端并退出。");
                Console.WriteLine();

                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 服务端异常: {ex.Message}");
                Console.ResetColor();
                return 4;
            }
            finally
            {
                await server.CloseAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] P2P 服务端已停止。");
            }

            return 0;
        }

        #endregion

        #region 帮助与环境检测

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }

        private static void CheckDokanyDriver()
        {
            try
            {
                var dokan = new Dokan(null!);
                var dokanVer = dokan.Version;
                var driverVer = dokan.DriverVersion;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.WriteLine("[警告] 未检测到 Dokany 运行时驱动（或驱动版本过旧）！");
                Console.WriteLine($"详情: {ex.Message}");
                Console.WriteLine("Master 端需要安装 Dokany 驱动才能将远程磁盘挂载为 Windows 原生盘符。");
                Console.WriteLine("请前往 Dokany 官网或 GitHub 页面下载安装: https://github.com/dokan-dev/dokany/releases");
                Console.WriteLine("--------------------------------------------------------------------------------\n");
                Console.ResetColor();
            }
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
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli - Master 挂载端 (v{version}) <<\n");
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
            Console.WriteLine($"               >> ApeFree.DonateDisk.WinCli - Slave 分享端 (v{version}) <<\n");
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
  ApeFree.DonateDisk.WinCli -m [Master选项]     以 Master 挂载端模式运行
  ApeFree.DonateDisk.WinCli -s [Slave选项]      以 Slave 分享端模式运行
  ApeFree.DonateDisk.WinCli --p2p-server [选项] 启动独立 P2P 引导与中继服务端
  ApeFree.DonateDisk.WinCli                     启动基于 PyramidMenu 的交互式分层向导

--------------------------------------------------------------------------------
【Master 挂载端模式选项 (-m, --master)】:
  -l, --listen              作为服务端被动监听（默认网络模式）
  -c, --connect             作为客户端主动连接 Slave 机器（正向挂载模式）
  -h, --host <IP/域名>      目标 Slave 机器地址（指定此项自动切换为主动连接 Connect 模式）
  -p, --port <端口>         本地监听端口或目标 Slave 端口（默认 9821）
  -i, --ip <IP地址>         本地监听绑定的网络 IP（默认 0.0.0.0）
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
    # 模式 A: Master 服务端监听 (反向分享，等待 Slave 连入)
    ApeFree.DonateDisk.WinCli -m -p 9821 -d Z

    # 模式 B: Master 主动拨号连接 (正向挂载，主动连上 Slave)
    ApeFree.DonateDisk.WinCli -m -h 192.168.1.50 -p 9821 -d Z

    # 模式 C: P2P 模式 (双方穿透 Relay Server)
    ApeFree.DonateDisk.WinCli -m -p2p --server ""127.0.0.1:9831"" -d Z

--------------------------------------------------------------------------------
【Slave 客户端分享模式选项 (-s, --slave)】:
  -c, --connect             作为客户端主动连入 Master（默认网络模式）
  -l, --listen              作为服务端被动监听（正向受控服务模式，等待 Master 连入）
  -h, --host <IP/域名>      目标 Master 端主机 IP 地址（默认 127.0.0.1，Connect 模式）
  -p, --port <端口>         Master 端端口或本地监听端口（默认 9821）
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
    # 模式 A: Slave 主动拨号连入 Master (反向分享，推荐跨公网)
    ApeFree.DonateDisk.WinCli -s -h 127.0.0.1 -p 9821 -d ""C:\Test"" -n ""Test""

    # 模式 B: Slave 服务端被动监听 (正向受控服务，允许多个 Master 同时挂载)
    ApeFree.DonateDisk.WinCli -s -l -p 9821 -d ""C:\Test"" -n ""Test""

    # 模式 C: P2P 模式 (双方穿透 Relay Server)
    ApeFree.DonateDisk.WinCli -s -p2p --server ""127.0.0.1:9831"" -d ""C:\Test""

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
            PrintOverallHelp();
        }

        private static void PrintSlaveHelp()
        {
            PrintOverallHelp();
        }

        private static void PrintP2PServerHelp()
        {
            PrintOverallHelp();
        }

        #endregion
    }
}
