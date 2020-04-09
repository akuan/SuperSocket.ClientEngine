using log4net;
using SuperSocket.ProtoBase;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SuperSocket.ClientEngine
{
    public class EasyClient : EasyClientBase
    {
        private Action<IPackageInfo> m_Handler;
        private static readonly ILog log = LogManager.GetLogger(typeof(EasyClient));

        private Timer SocketDetecterTimer = null;
        public int ConnectCheckInterval { get; set; } = 3000;

        private   void DetecterCallback(object stateInfo)
        {
            try
            {
                if (Session != null)
                {
                    if (Session.Socket == null)
                    {
                        Session.Reconnect();
                        return;
                    }
                    if (!Session.Socket.Connected)
                    {
                        if (!Session.IsSocketConnected())
                        {
                            Session.Reconnect();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EE.LogError("Detecter connection Error", ex, log);
            }
        } 
        public void Initialize<TPackageInfo>(IReceiveFilter<TPackageInfo> receiveFilter, Action<TPackageInfo> handler)
            where TPackageInfo : IPackageInfo
        {           
            PipeLineProcessor = new DefaultPipelineProcessor<TPackageInfo>(receiveFilter);
            m_Handler = (p) => handler((TPackageInfo)p);
            //
            TimerCallback detecterDelegate = new TimerCallback(DetecterCallback);
            SocketDetecterTimer = new Timer(detecterDelegate, null, 500, ConnectCheckInterval);
        }

        protected override void HandlePackage(IPackageInfo package)
        {
            var handler = m_Handler;
            if (handler == null)
                return;
            handler(package);
        }
        public override async Task<bool> Close()
        {
            await base.Close();
            if (SocketDetecterTimer != null)
            {
                SocketDetecterTimer.Dispose();
            }
            return true;
        }
    }

    public class EasyClient<TPackageInfo> : EasyClientBase
        where TPackageInfo : IPackageInfo
    {
        public event EventHandler<PackageEventArgs<TPackageInfo>> NewPackageReceived;

        public EasyClient()
        {
            
        }

        public virtual void Initialize(IReceiveFilter<TPackageInfo> receiveFilter)
        {
            PipeLineProcessor = new DefaultPipelineProcessor<TPackageInfo>(receiveFilter);
        }

        protected override void HandlePackage(IPackageInfo package)
        {
            var handler = NewPackageReceived;

            if (handler == null)
                return;

            handler(this, new PackageEventArgs<TPackageInfo>((TPackageInfo)package));
        }
        
    }
}
