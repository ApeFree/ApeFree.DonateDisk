using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using ApeFree.PyramidMenu.Menus;
using ApeFree.PyramidMenu.Options;
using ApeFree.PyramidMenu.Scenes;
using System;
using System.IO;

namespace ApeFree.DonateDisk.WinCli.Common
{
    /// <summary>
    /// 向导执行结果
    /// </summary>
    public enum WizardRunMode
    {
        None,
        Master,
        Slave,
        P2PServer
    }

    public class WizardResult
    {
        public WizardRunMode Mode { get; set; }
        public DiskMasterOptions? MasterOptions { get; set; }
        public DiskSlaveOptions? SlaveOptions { get; set; }
        public int P2PServerPort { get; set; } = 9831;
    }

    /// <summary>
    /// 基于 ApeFree.PyramidMenu 状态机驱动的无参交互式配置向导
    /// </summary>
    public static class DonateDiskMenuWizard
    {
        private static void SafeClear()
        {
            try
            {
                if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
                {
                    Console.Clear();
                }
            }
            catch { }
        }

        public static WizardResult? Run()
        {
            var rootMenu = new WizardRootMenu();
            var scene = new Scene(rootMenu);

            while (!rootMenu.IsFinished)
            {
                SafeClear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("===============================================================");
                Console.WriteLine("       ApeFree.DonateDisk 交互式向导 (基于 PyramidMenu 引擎)");
                Console.WriteLine("===============================================================");
                Console.ResetColor();

                var rendered = scene.RenderCurrentMenu();
                Console.WriteLine(rendered);

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write("\n请根据提示输入序号 (直接按 [回车] 默认选择 [1]，输入 'q' 退出): ");
                Console.ResetColor();

                var rawInput = Console.ReadLine();
                if (rawInput == null)
                {
                    return null;
                }

                var input = rawInput.Trim();
                if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (string.IsNullOrEmpty(input))
                {
                    input = "1";
                }

                var result = scene.Input(new Packet(input));
                if (!result.IsSuccessful && !rootMenu.IsFinished)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[提示] 输入无效: {result.PromptText ?? "请重新选择"}");
                    Console.ResetColor();
                    Console.WriteLine("按任意键继续...");
                    try
                    {
                        if (!Console.IsInputRedirected)
                        {
                            Console.ReadKey(true);
                        }
                    }
                    catch { }
                }
            }

            return rootMenu.FinalResult;
        }

        #region 菜单实现

        public class WizardRootMenu : TextMenu
        {
            public bool IsFinished { get; set; }
            public WizardResult? FinalResult { get; set; }

            protected override void OnCreate()
            {
                Title = "选择运行角色";
                TextContent = "请选择当前节点要在本机扮演的角色身份：";

                AddMenuOption("Master 磁盘挂载端 (挂载远程磁盘为 Windows 本地盘符)", typeof(MasterTransportSelectMenu));
                AddMenuOption("Slave 本地磁盘分享端 (将本地目录安全分享给远端挂载)", typeof(SlaveTransportSelectMenu));
                AddMenuOption("P2P 引导中继端 (启动独立公网 Relay Server)", typeof(P2PServerConfigMenu));
                AddDivider();
                AddCustomOption("退出程序", (ctx, opt, packet, ct) =>
                {
                    IsFinished = true;
                    FinalResult = null;
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });
            }
        }

        #region Master 菜单分支

        internal class MasterTransportSelectMenu : TextMenu
        {
            protected override void OnCreate()
            {
                Title = "Master 挂载端 - 选择通信模式";
                TextContent = "请选择 Master 端与 Slave 端的网络连通方式：";

                AddMenuOption("被动监听等待 (Listen，默认反向分享：等待 Slave 拨号连入)", typeof(MasterListenConfigMenu));
                AddMenuOption("主动拨号连接 (Connect，正向直连：主动连接目标 Slave 机器)", typeof(MasterConnectConfigMenu));
                AddMenuOption("P2P 中继穿透模式 (双方均位于 NAT 内部，通过公网 Relay 打通)", typeof(MasterP2PConfigMenu));
                AddDivider();
                AddBackOption("返回上级菜单");
            }
        }

        internal class MasterListenConfigMenu : TextMenu
        {
            private string _host = "0.0.0.0";
            private int _port = 9821;
            private char _driveLetter = 'Z';
            private bool _isRemovable = true;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Master 服务端监听配置 (反向分享模式)";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - 监听网络: {_host}:{_port}\r\n" +
                              $"  - 首选盘符: {_driveLetter}:\\ (若占用自动顺延)\r\n" +
                              $"  - 挂载类型: {(_isRemovable ? "可移动驱动器 (Removable)" : "本地固定磁盘 (Fixed)")}";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 使用上述预设直接启动 Master", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Master,
                            MasterOptions = new DiskMasterOptions
                            {
                                TransportMode = NetworkTransportMode.Listen,
                                ListenHost = _host,
                                ListenPort = _port,
                                PreferredDriveLetter = _driveLetter,
                                MountAsRemovable = _isRemovable
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改监听端口 (当前: {_port})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入新的监听端口 [当前: {_port}]: ");
                    var pStr = Console.ReadLine()?.Trim();
                    if (int.TryParse(pStr, out var newPort) && newPort > 0 && newPort <= 65535)
                    {
                        _port = newPort;
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改首选盘符 (当前: {_driveLetter}:\\)", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入首选驱动器盘符 (D-Z) [当前: {_driveLetter}]: ");
                    var lStr = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(lStr) && char.IsLetter(lStr[0]))
                    {
                        _driveLetter = char.ToUpperInvariant(lStr[0]);
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"切换挂载磁盘类型 (当前: {(_isRemovable ? "可移动磁盘" : "固定磁盘")})", (ctx, opt, packet, ct) =>
                {
                    _isRemovable = !_isRemovable;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        internal class MasterConnectConfigMenu : TextMenu
        {
            private string _targetHost = "127.0.0.1";
            private int _targetPort = 9821;
            private char _driveLetter = 'Z';
            private bool _isRemovable = true;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Master 主动连接配置 (正向挂载模式)";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - 目标 Slave: {_targetHost}:{_targetPort}\r\n" +
                              $"  - 首选盘符:   {_driveLetter}:\\ (若占用自动顺延)\r\n" +
                              $"  - 挂载类型:   {(_isRemovable ? "可移动驱动器 (Removable)" : "本地固定磁盘 (Fixed)")}\r\n" +
                              $"  - 容错机制:   自动断线无限重试与自动重挂载 (每 3 秒)";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 主动拨号连接目标 Slave 并挂载", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Master,
                            MasterOptions = new DiskMasterOptions
                            {
                                TransportMode = NetworkTransportMode.Connect,
                                TargetHost = _targetHost,
                                TargetPort = _targetPort,
                                PreferredDriveLetter = _driveLetter,
                                MountAsRemovable = _isRemovable
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改目标 Slave 地址 (当前: {_targetHost}:{_targetPort})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入目标 Slave IP/主机名 [当前: {_targetHost}]: ");
                    var h = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(h)) _targetHost = h;

                    Console.Write($"请输入目标 Slave 端口 [当前: {_targetPort}]: ");
                    var pStr = Console.ReadLine()?.Trim();
                    if (int.TryParse(pStr, out var newPort) && newPort > 0 && newPort <= 65535) _targetPort = newPort;

                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改首选盘符 (当前: {_driveLetter}:\\)", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入首选驱动器盘符 (D-Z) [当前: {_driveLetter}]: ");
                    var lStr = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(lStr) && char.IsLetter(lStr[0]))
                    {
                        _driveLetter = char.ToUpperInvariant(lStr[0]);
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        internal class MasterP2PConfigMenu : TextMenu
        {
            private string _serverAddr = "127.0.0.1:9831";
            private char _driveLetter = 'Z';

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Master P2P 穿透模式配置";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - P2P 中继服务端: {_serverAddr}\r\n" +
                              $"  - 首选盘符:       {_driveLetter}:\\\r\n" +
                              $"  - 凭据机制:       内置预设种子 (自动配对)";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 连接公网 Relay 并等待 Slave 自动挂载", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Master,
                            MasterOptions = new DiskMasterOptions
                            {
                                TransportMode = NetworkTransportMode.P2P,
                                UseP2P = true,
                                P2PServerAddress = _serverAddr,
                                PreferredDriveLetter = _driveLetter
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改 P2P 中继服务端地址 (当前: {_serverAddr})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入 P2P 中继服务端地址 (如 1.2.3.4:9831) [当前: {_serverAddr}]: ");
                    var s = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(s)) _serverAddr = s;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        #endregion

        #region Slave 菜单分支

        internal class SlaveTransportSelectMenu : TextMenu
        {
            protected override void OnCreate()
            {
                Title = "Slave 分享端 - 选择通信模式";
                TextContent = "请选择 Slave 端与 Master 端的网络连通方式：";

                AddMenuOption("主动拨号连接 (Connect，默认反向穿透：主动连接 Master)", typeof(SlaveConnectConfigMenu));
                AddMenuOption("被动监听等待 (Listen，正向受控服务：本地监听等待 Master 接入)", typeof(SlaveListenConfigMenu));
                AddMenuOption("P2P 中继穿透模式 (双方均位于 NAT 内部，通过公网 Relay 打通)", typeof(SlaveP2PConfigMenu));
                AddDivider();
                AddBackOption("返回上级菜单");
            }
        }

        internal class SlaveConnectConfigMenu : TextMenu
        {
            private string _targetHost = "127.0.0.1";
            private int _targetPort = 9821;
            private string _localDir = Environment.CurrentDirectory;
            private bool _isReadOnly = false;
            private string? _volumeLabel;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Slave 主动连出配置 (反向穿透模式)";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - 目标 Master: {_targetHost}:{_targetPort}\r\n" +
                              $"  - 本地共享目录: {_localDir}\r\n" +
                              $"  - 只读保护:     {(_isReadOnly ? "是 (只读，禁止写入)" : "否 (支持读写)")}\r\n" +
                              $"  - 磁盘卷标:     {(string.IsNullOrEmpty(_volumeLabel) ? "自动 (取目录名)" : _volumeLabel)}";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 主动连接 Master 并共享本地目录", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Slave,
                            SlaveOptions = new DiskSlaveOptions
                            {
                                TransportMode = NetworkTransportMode.Connect,
                                TargetHost = _targetHost,
                                TargetPort = _targetPort,
                                LocalDirectory = _localDir,
                                IsReadOnly = _isReadOnly,
                                VolumeLabel = _volumeLabel
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改本地共享物理目录 (当前: {_localDir})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入待共享的本地绝对或相对路径 [当前: {_localDir}]: ");
                    var dir = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var full = Path.GetFullPath(dir);
                        if (!Directory.Exists(full))
                        {
                            try { Directory.CreateDirectory(full); } catch { }
                        }
                        _localDir = full;
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改目标 Master 地址 (当前: {_targetHost}:{_targetPort})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入目标 Master IP/主机名 [当前: {_targetHost}]: ");
                    var h = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(h)) _targetHost = h;

                    Console.Write($"请输入目标 Master 端口 [当前: {_targetPort}]: ");
                    var pStr = Console.ReadLine()?.Trim();
                    if (int.TryParse(pStr, out var newPort) && newPort > 0 && newPort <= 65535) _targetPort = newPort;

                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"切换只读模式 (当前: {(_isReadOnly ? "只读保护" : "允许读写")})", (ctx, opt, packet, ct) =>
                {
                    _isReadOnly = !_isReadOnly;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改磁盘卷标名称 (当前: {(string.IsNullOrEmpty(_volumeLabel) ? "(自动取目录名)" : _volumeLabel)})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入自定义虚拟磁盘卷标名称 (直接回车保持默认) [当前: {_volumeLabel}]: ");
                    var vl = Console.ReadLine()?.Trim();
                    _volumeLabel = string.IsNullOrEmpty(vl) ? null : vl;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        internal class SlaveListenConfigMenu : TextMenu
        {
            private string _host = "0.0.0.0";
            private int _port = 9821;
            private string _localDir = Environment.CurrentDirectory;
            private bool _isReadOnly = false;
            private string? _volumeLabel;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Slave 服务端监听配置 (正向服务受控模式)";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - 监听网络:     {_host}:{_port}\r\n" +
                              $"  - 本地共享目录: {_localDir}\r\n" +
                              $"  - 只读保护:     {(_isReadOnly ? "是 (只读，禁止写入)" : "否 (支持读写)")}\r\n" +
                              $"  - 多端并发:     完全允许多个 Master 同时连入挂载 (句柄安全隔离)";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 开放端口监听等待 Master 接入", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Slave,
                            SlaveOptions = new DiskSlaveOptions
                            {
                                TransportMode = NetworkTransportMode.Listen,
                                ListenHost = _host,
                                ListenPort = _port,
                                LocalDirectory = _localDir,
                                IsReadOnly = _isReadOnly,
                                VolumeLabel = _volumeLabel
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改本地共享物理目录 (当前: {_localDir})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入待共享的本地绝对或相对路径 [当前: {_localDir}]: ");
                    var dir = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var full = Path.GetFullPath(dir);
                        if (!Directory.Exists(full))
                        {
                            try { Directory.CreateDirectory(full); } catch { }
                        }
                        _localDir = full;
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改本地监听端口 (当前: {_port})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入本地监听端口 [当前: {_port}]: ");
                    var pStr = Console.ReadLine()?.Trim();
                    if (int.TryParse(pStr, out var newPort) && newPort > 0 && newPort <= 65535) _port = newPort;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"切换只读模式 (当前: {(_isReadOnly ? "只读保护" : "允许读写")})", (ctx, opt, packet, ct) =>
                {
                    _isReadOnly = !_isReadOnly;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改磁盘卷标名称 (当前: {(string.IsNullOrEmpty(_volumeLabel) ? "(自动取目录名)" : _volumeLabel)})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入自定义虚拟磁盘卷标名称 (直接回车保持默认) [当前: {_volumeLabel}]: ");
                    var vl = Console.ReadLine()?.Trim();
                    _volumeLabel = string.IsNullOrEmpty(vl) ? null : vl;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        internal class SlaveP2PConfigMenu : TextMenu
        {
            private string _serverAddr = "127.0.0.1:9831";
            private string _localDir = Environment.CurrentDirectory;
            private bool _isReadOnly = false;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                UpdateMenu();
            }

            private void UpdateMenu()
            {
                Title = "Slave P2P 穿透模式配置";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - P2P 中继服务端: {_serverAddr}\r\n" +
                              $"  - 本地共享目录:   {_localDir}\r\n" +
                              $"  - 只读保护:       {(_isReadOnly ? "是" : "否")}";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 连接公网 Relay 并将本地目录分享给 Master", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.Slave,
                            SlaveOptions = new DiskSlaveOptions
                            {
                                TransportMode = NetworkTransportMode.P2P,
                                UseP2P = true,
                                P2PServerAddress = _serverAddr,
                                LocalDirectory = _localDir,
                                IsReadOnly = _isReadOnly
                            }
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改本地共享物理目录 (当前: {_localDir})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入待共享的本地绝对或相对路径 [当前: {_localDir}]: ");
                    var dir = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var full = Path.GetFullPath(dir);
                        if (!Directory.Exists(full))
                        {
                            try { Directory.CreateDirectory(full); } catch { }
                        }
                        _localDir = full;
                    }
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改 P2P 中继服务端地址 (当前: {_serverAddr})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入 P2P 中继服务端地址 [当前: {_serverAddr}]: ");
                    var s = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(s)) _serverAddr = s;
                    UpdateMenu();
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回选择其他通信模式");
            }
        }

        #endregion

        #region P2P Server 菜单

        internal class P2PServerConfigMenu : TextMenu
        {
            private int _port = 9831;

            protected override void OnStart(StartEventArgs e)
            {
                base.OnStart(e);
                Title = "P2P 引导与中继服务端配置 (Relay Server)";
                TextContent = $"【当前预设配置】\r\n" +
                              $"  - 监听端口: {_port}\r\n" +
                              $"  - 身份种子: 内置默认固定种子 (双方零配置直连)\r\n" +
                              $"  - 协议支持: TCP + QUIC + Circuit Relay v2";

                OptionList.Clear();
                AddCustomOption("[一键极速启动] 立即启动 P2P 中继服务端", (ctx, opt, packet, ct) =>
                {
                    var root = ctx.RootMenu as WizardRootMenu;
                    if (root != null)
                    {
                        root.FinalResult = new WizardResult
                        {
                            Mode = WizardRunMode.P2PServer,
                            P2PServerPort = _port
                        };
                        root.IsFinished = true;
                    }
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddCustomOption($"修改监听端口 (当前: {_port})", (ctx, opt, packet, ct) =>
                {
                    Console.Write($"\n请输入监听端口 [当前: {_port}]: ");
                    var pStr = Console.ReadLine()?.Trim();
                    if (int.TryParse(pStr, out var newPort) && newPort > 0 && newPort <= 65535) _port = newPort;
                    return System.Threading.Tasks.Task.FromResult(Result.Success(ctx, opt, packet));
                });

                AddDivider();
                AddBackOption("返回上级菜单");
            }
        }

        #endregion

        #endregion
    }
}
