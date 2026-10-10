using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
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
        private enum RunMode
        {
            None,
            Master,
            Slave
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

            // 2. 检测运行模式（Master 或 Slave）与 P2P 标志
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
            if (mode == RunMode.Master)
            {
                return await RunMasterAsync(filteredArgs, appVersion, isP2P);
            }
            else
            {
                return await RunSlaveAsync(filteredArgs, appVersion, isP2P);
            }
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
            }

            remainingArgs = argList.ToArray();
            return mode;
        }

        private static RunMode PromptInteractiveMode()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================== 请选择运行模式 ===================");
            Console.ResetColor();
            Console.WriteLine("  [1] Master 挂载服务端 - 监听网络并自动挂载远程 Slave 捐赠的磁盘");
            Console.WriteLine("  [2] Slave  分享客户端 - 主动将本地目录分享给 Master 挂载使用");
            Console.WriteLine("======================================================");
            Console.Write("请输入选项 [1/2] (默认 1): ");
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

            // 检查本地 Dokany 驱动与运行时环境
            CheckDokanyDriver();

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
                Console.WriteLine($"  P2P 监听端口:    {options.P2PPort}");
                Console.WriteLine($"  Relay 中继穿透:  {(options.EnableRelay ? "已启用" : "未启用")}");
                Console.WriteLine($"  DHT 节点发现:    {(options.EnableDht ? "已启用" : "未启用")}");
                Console.WriteLine($"  mDNS 局域网发现: {(options.EnableMdns ? "已启用" : "未启用")}");
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

            var listenDesc = options.UseP2P ? $"P2P 端口 {options.P2PPort}" : $"TCP {options.ListenHost}:{options.ListenPort}";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在启动 Master 服务端监听 ({listenDesc})...");

            try
            {
                await masterNode.StartAsync();

                if (options.UseP2P)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (!string.IsNullOrEmpty(masterNode.P2PLocalPeerId))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [P2P 就绪] 本地 PeerId: {masterNode.P2PLocalPeerId}");
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
            int p2pPort = 4001;
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
                else if (arg == "--p2p-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pPort))
                {
                    p2pPort = pPort;
                    i++;
                }
                else if (arg == "--relay")
                {
                    enableRelay = true;
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
            int p2pPort = 4001;
            string host = "0.0.0.0";
            bool enableRelay = false;

            if (useP2P)
            {
                Console.Write("请输入 P2P 本地监听端口 (默认 4001): ");
                var pInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(pInput) && int.TryParse(pInput, out var pp))
                {
                    p2pPort = pp;
                }

                Console.Write("是否启用 Relay 中继以支持 NAT 穿透？(y/N): ");
                var relayInput = Console.ReadLine()?.Trim();
                enableRelay = string.Equals(relayInput, "y", StringComparison.OrdinalIgnoreCase);
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
                P2PPort = p2pPort,
                EnableRelay = enableRelay
            };
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

        #endregion

        #region Slave 运行逻辑

        private static async Task<int> RunSlaveAsync(string[] args, string appVersion, bool isP2P)
        {
            Console.Title = $"ApeFree.DonateDisk.WinCli v{appVersion} - [Slave 模式] 远程磁盘主动分享客户端";
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
                var targetAddr = !string.IsNullOrEmpty(options.P2PRemoteAddress)
                    ? options.P2PRemoteAddress
                    : $"/ip4/{options.TargetHost}/tcp/{options.P2PPort}" + (!string.IsNullOrEmpty(options.P2PPeerId) ? $"/p2p/{options.P2PPeerId}" : "");
                Console.WriteLine($"  目标 Multiaddr:  {targetAddr}");
                Console.WriteLine($"  Relay 中继穿透:  {(options.EnableRelay ? "已启用" : "未启用")}");
                Console.WriteLine($"  DHT 节点发现:    {(options.EnableDht ? "已启用" : "未启用")}");
                Console.WriteLine($"  mDNS 局域网发现: {(options.EnableMdns ? "已启用" : "未启用")}");
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
                ? (!string.IsNullOrEmpty(options.P2PRemoteAddress) ? options.P2PRemoteAddress : $"{options.TargetHost}:{options.P2PPort}")
                : $"{options.TargetHost}:{options.TargetPort}";

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 正在连接 Master ({connectTarget})...");

            try
            {
                var success = await slaveNode.StartAsync(cts.Token);
                if (!success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [连接失败] 无法连接到 Master 端 ({connectTarget})。请检查 Master 是否已启动或网络端口/地址是否正确。");
                    Console.ResetColor();
                    return 2;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 磁盘捐赠已启动！按 Ctrl+C 可停止分享并安全退出。");
                Console.ResetColor();

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

            string host = "127.0.0.1";
            int port = 9821;
            string? dir = null;
            string? label = null;
            bool isReadOnly = false;

            // P2P 专用选项
            bool useP2P = isP2P;
            string? p2pRemoteAddr = null;
            string? p2pPeerId = null;
            int p2pPort = 4001;
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
                else if ((arg == "-addr" || arg == "--remote-addr") && i + 1 < args.Length)
                {
                    p2pRemoteAddr = args[++i];
                    useP2P = true;
                }
                else if ((arg == "-peer" || arg == "--peer-id") && i + 1 < args.Length)
                {
                    p2pPeerId = args[++i];
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
                P2PRemoteAddress = p2pRemoteAddr,
                P2PPeerId = p2pPeerId,
                P2PPort = p2pPort,
                EnableRelay = enableRelay,
                EnableDht = enableDht,
                EnableMdns = enableMdns,
                EnableQuic = enableQuic,
                BootstrapPeers = bootstrapPeers
            };
        }

        private static DiskSlaveOptions? PromptInteractiveSlaveOptions(bool isP2P)
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
            string? p2pRemoteAddr = null;
            int p2pPort = 4001;
            bool enableRelay = false;

            if (useP2P)
            {
                Console.Write("请输入 Master 端 Multiaddr 地址或主机 IP (默认 127.0.0.1): ");
                var addrInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(addrInput))
                {
                    if (addrInput.StartsWith("/"))
                    {
                        p2pRemoteAddr = addrInput;
                    }
                    else
                    {
                        host = addrInput;
                    }
                }

                if (string.IsNullOrWhiteSpace(p2pRemoteAddr))
                {
                    Console.Write("请输入 Master 端 P2P 端口号 (默认 4001): ");
                    var pInput = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(pInput) && int.TryParse(pInput, out var pp))
                    {
                        p2pPort = pp;
                    }
                }

                Console.Write("是否启用 Relay 中继进行 NAT 穿透？(y/N): ");
                var relayInput = Console.ReadLine()?.Trim();
                enableRelay = string.Equals(relayInput, "y", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Console.Write("请输入 Master 端主机 IP 地址 (默认 127.0.0.1): ");
                var hostInput = Console.ReadLine()?.Trim();
                host = string.IsNullOrWhiteSpace(hostInput) ? "127.0.0.1" : hostInput;

                Console.Write("请输入 Master 端端口号 (默认 9821): ");
                var portInput = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out var p))
                {
                    port = p;
                }
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
                IsReadOnly = isReadOnly,
                UseP2P = useP2P,
                P2PRemoteAddress = p2pRemoteAddr,
                P2PPort = p2pPort,
                EnableRelay = enableRelay
            };
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

        private static void PrintOverallHelp()
        {
            var version = GetAppVersion();
            Console.WriteLine($@"
ApeFree.DonateDisk.WinCli (v{version}) - 远程虚拟磁盘挂载与分享综合工具

用法:
  ApeFree.DonateDisk.WinCli -m [Master选项]   以 Master 挂载服务端模式运行
  ApeFree.DonateDisk.WinCli -s [Slave选项]    以 Slave 分享客户端模式运行
  ApeFree.DonateDisk.WinCli                   交互模式引导选择与启动

--------------------------------------------------------------------------------
【Master 服务端模式选项 (-m, --master)】:
  -i, --ip <IP地址>        本地监听网络地址（默认 0.0.0.0）
  -p, --port <端口>        本地 TCP 监听端口（默认 9821）
  -d, --drive <盘符>       首选挂载盘符（默认 Z，若被占用自动向下顺延）
  --fixed                  挂载为本地固定硬盘（默认挂载为可移动磁盘 RemovableDrive）

  [P2P 专用选项]:
  -p2p, --p2p              启用 P2P (Libp2p) 去中心化监听模式
  --p2p-port <端口>        P2P 本地监听端口（默认 4001）
  --relay                  启用 Relay 中继协议以支持 NAT 穿透
  --dht                    启用 Kademlia DHT 分布式节点发现
  --mdns                   启用 mDNS 局域网节点发现
  --quic                   启用 QUIC 传输协议
  --bootstrap <Multiaddr>  添加引导节点地址

示例:
  ApeFree.DonateDisk.WinCli -m -p 9821 -d Z
  ApeFree.DonateDisk.WinCli -m -p2p --p2p-port 4001 -d Z

--------------------------------------------------------------------------------
【Slave 客户端模式选项 (-s, --slave)】:
  -h, --host <IP/域名>     Master 端主机 IP 地址（默认 127.0.0.1）
  -p, --port <端口>        Master 端服务端口（默认 9821）
  -d, --dir <本地路径>     待分享给 Master 的本地物理文件夹路径（必需）
  -n, --name <卷标名称>    挂载后显示的虚拟磁盘名称（默认使用目录名）
  -r, --readonly           以只读保护模式分享（禁止远端修改和删除文件）

  [P2P 专用选项]:
  -p2p, --p2p              启用 P2P (Libp2p) 去中心化点对点通信模式
  -addr, --remote-addr <地址> 远程 Master 节点的 Multiaddr 地址
  -peer, --peer-id <PeerId> 目标 Master 节点的 PeerId
  --p2p-port <端口>        目标 P2P 端口（默认 4001）
  --relay                  启用 Relay 中继协议以支持 NAT 穿透
  --dht                    启用 Kademlia DHT 节点发现
  --mdns                   启用 mDNS 局域网发现
  --quic                   启用 QUIC 传输协议
  --bootstrap <Multiaddr>  添加引导节点地址

示例:
  # 标准 TCP 模式
  ApeFree.DonateDisk.WinCli -s -d ""D:\SharedData"" -h 192.168.1.100 -p 9821

  # P2P 模式（直接指定 Multiaddress）
  ApeFree.DonateDisk.WinCli -s -p2p -d ""D:\SharedData"" -addr ""/ip4/192.168.1.100/tcp/4001/p2p/12D3KooW...""

  # P2P 模式（指定主机、端口与 PeerId）
  ApeFree.DonateDisk.WinCli -s -p2p -d ""D:\SharedData"" -h 192.168.1.100 --p2p-port 4001 --peer-id 12D3KooW...
");
        }

        private static void PrintMasterHelp()
        {
            Console.WriteLine(@"
Master 服务端模式选项:
  -i, --ip <IP地址>        本地监听网络地址（默认 0.0.0.0）
  -p, --port <端口>        本地 TCP 监听端口（默认 9821）
  -d, --drive <盘符>       首选挂载盘符（默认 Z，若被占用自动向下顺延）
  --fixed                  挂载为本地固定硬盘（默认挂载为可移动磁盘 RemovableDrive）

P2P 专用选项:
  -p2p, --p2p              启用 P2P (Libp2p) 监听模式
  --p2p-port <端口>        P2P 本地监听端口（默认 4001）
  --relay                  启用 Relay 中继协议
  --dht                    启用 Kademlia DHT 发现
  --mdns                   启用 mDNS 局域网发现
  --quic                   启用 QUIC 传输
  --bootstrap <Multiaddr>  添加引导节点
  -?, --help               显示本帮助信息

示例:
  ApeFree.DonateDisk.WinCli -m -p 9821 -d Z
  ApeFree.DonateDisk.WinCli -m -p2p --p2p-port 4001 -d Z
");
        }

        private static void PrintSlaveHelp()
        {
            Console.WriteLine(@"
Slave 客户端模式选项:
  -h, --host <IP/域名>     Master 端主机 IP 地址（默认 127.0.0.1）
  -p, --port <端口>        Master 端服务端口（默认 9821）
  -d, --dir <本地路径>     待分享给 Master 的本地物理文件夹路径（必需）
  -n, --name <卷标名称>    挂载后显示的虚拟磁盘名称（默认使用目录名）
  -r, --readonly           以只读保护模式分享（禁止远端修改和删除文件）

P2P 专用选项:
  -p2p, --p2p              启用 P2P (Libp2p) 点对点传输模式
  -addr, --remote-addr <地址> 目标 Master 的 Multiaddr 地址
  -peer, --peer-id <PeerId> 目标 Master 的 PeerId
  --p2p-port <端口>        目标 P2P 端口（默认 4001）
  --relay                  启用 Relay 中继协议
  --dht                    启用 Kademlia DHT 发现
  --mdns                   启用 mDNS 局域网发现
  --quic                   启用 QUIC 传输
  --bootstrap <Multiaddr>  添加引导节点
  -?, --help               显示本帮助信息

示例:
  ApeFree.DonateDisk.WinCli -s -d ""D:\SharedData"" -h 192.168.1.100 -p 9821
  ApeFree.DonateDisk.WinCli -s -p2p -d ""D:\SharedData"" -addr ""/ip4/192.168.1.100/tcp/4001/p2p/12D3KooW...""
");
        }

        #endregion
    }
}
