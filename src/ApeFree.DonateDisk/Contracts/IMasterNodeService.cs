using System;

namespace ApeFree.DonateDisk.Contracts
{
    /// <summary>
    /// Master 节点协同与控制服务契约。
    /// 由 Master 端实现并暴露，Slave 端在接入中继或直连网络后可主动通知 Master 节点准备挂载。
    /// </summary>
    public interface IMasterNodeService
    {
        /// <summary>
        /// 通知 Master 端：Slave 节点已完全就绪，可立即发起虚拟磁盘挂载
        /// </summary>
        void NotifySlaveReady();
    }
}
