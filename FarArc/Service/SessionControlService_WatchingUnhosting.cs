using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using FarArc.Model;
using FarArc.Model.Protocol;
using FarArc.Model.Protocol.Base;
using FarArc.Model.ProtocolRunner;
using FarArc.Model.ProtocolRunner.Default;
using FarArc.Utils;
using FarArc.View;
using FarArc.View.Host;
using FarArc.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace FarArc.Service
{
    public partial class SessionControlService
    {
        private static readonly HashSet<Process> _watching = new HashSet<Process>();
        public static void AddUnHostingWatch(Process? process, ProtocolBase protocol)
        {
            if(process == null) return;
            if (string.IsNullOrWhiteSpace(protocol.CommandAfterDisconnected)) return;
            try
            {
                // 添加对进程 PID 的监控，进程关闭时回调
                process.Exited += (sender, args) => { protocol.RunScriptAfterDisconnected(); };
                process.EnableRaisingEvents = true;
                _watching.Add(process);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning(e);
            }

            var ps = Process.GetProcesses();
            foreach (var p in _watching.ToArray())
            {
                try
                {
                    // 查找 process.Pid 是否存在
                    if (ps.Any(x => x.Id == p.Id)) continue;
                    _watching.Remove(p);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }
    }
}