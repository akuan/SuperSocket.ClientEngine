using log4net;
using SuperSocket.ProtoBase;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SuperSocket.ClientEngine
{
    public abstract class TcpClientSession : ClientSession
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(TcpClientSession));
        protected string HostName { get; private set; }
        private ManualResetEvent ConnectTimeout= new ManualResetEvent(false);       
        private bool m_InConnecting = false;
        private EndPoint m_RemoteEndPoint;

        public TcpClientSession()
            : base()
        {

        }

#if !SILVERLIGHT
        public override EndPoint LocalEndPoint
        {
            get
            {
                return base.LocalEndPoint;
            }

            set
            {
                if (m_InConnecting || IsConnected)
                {
                    log.Error("Set LocalEndPoint Fail: You cannot set LocalEndPoint after you start the connection.");
                    // throw new Exception("You cannot set LocalEndPoint after you start the connection.");
                    return;
                }

                base.LocalEndPoint = value;
            }
        }
#endif

        public override int ReceiveBufferSize
        {
            get
            {
                return base.ReceiveBufferSize;
            }

            set
            {
                if (Buffer.Array != null)
                {
                    log.Error("ReceiveBufferSize cannot be set after the socket has been connected!");
                    //throw new Exception("ReceiveBufferSize cannot be set after the socket has been connected!");
                }
                  

                base.ReceiveBufferSize = value;
            }
        }

        protected virtual bool IsIgnorableException(Exception e)
        {
            if (e is System.ObjectDisposedException)
                return true;

            if (e is NullReferenceException)
                return true;

            return false;
        }

        protected bool IsIgnorableSocketError(int errorCode)
        {
            //SocketError.Shutdown = 10058
            //SocketError.ConnectionAborted = 10053
            //SocketError.ConnectionReset = 10054
            //SocketError.OperationAborted = 995
            if (errorCode == 10058 || errorCode == 10053 || errorCode == 10054 || errorCode == 995)
                return true;

            return false;
        }

#if SILVERLIGHT && !WINDOWS_PHONE
        private SocketClientAccessPolicyProtocol m_ClientAccessPolicyProtocol = SocketClientAccessPolicyProtocol.Http;

        public SocketClientAccessPolicyProtocol ClientAccessPolicyProtocol
        {
            get { return m_ClientAccessPolicyProtocol; }
            set { m_ClientAccessPolicyProtocol = value; }
        }
#endif

        protected abstract void SocketEventArgsCompleted(object sender, SocketAsyncEventArgs e);

        public override void Connect(EndPoint remoteEndPoint)
        {
            if (remoteEndPoint == null)
            {
                log.Error("Connect Error :remoteEndPoint  is null.");
                throw new ArgumentNullException("remoteEndPoint is null");
            }
               
            m_RemoteEndPoint = remoteEndPoint;
            var dnsEndPoint = remoteEndPoint as DnsEndPoint;

            if (dnsEndPoint != null)
            {
                var hostName = dnsEndPoint.Host;

                if (!string.IsNullOrEmpty(hostName))
                    HostName = hostName;
            }

            if (m_InConnecting)
            {
                log.Error("The socket is connecting, cannot connect again!");
                return;
                //throw new Exception("The socket is connecting, cannot connect again!");
            } 

            if (Client != null)
            {
                log.Error("The socket is connected, you needn't connect again!");
                throw new Exception("The socket is connected, you needn't connect again!");
            }
              

            //If there is a proxy set, connect the proxy server by proxy connector
            if (Proxy != null)
            {
                Proxy.Completed += new EventHandler<ProxyEventArgs>(Proxy_Completed);
                Proxy.Connect(remoteEndPoint);
                m_InConnecting = true;
                return;
            }

            m_InConnecting = true;

            //WindowsPhone doesn't have this property
#if SILVERLIGHT && !WINDOWS_PHONE
            remoteEndPoint.ConnectAsync(ClientAccessPolicyProtocol, ProcessConnect, null);
#elif WINDOWS_PHONE
            remoteEndPoint.ConnectAsync(ProcessConnect, null);
#else
            remoteEndPoint.ConnectAsync(LocalEndPoint, ProcessConnect, null);
#endif
        }

        void Proxy_Completed(object sender, ProxyEventArgs e)
        {
            Proxy.Completed -= new EventHandler<ProxyEventArgs>(Proxy_Completed);

            if (e.Connected)
            {
                SocketAsyncEventArgs se = null;
                if (e.TargetHostName != null)
                {
                    se = new SocketAsyncEventArgs();
                    se.RemoteEndPoint = new DnsEndPoint(e.TargetHostName, 0);
                }
                ProcessConnect(e.Socket, null, se, null);
                return;
            }

            OnError(new Exception("proxy error", e.Exception));
            m_InConnecting = false;
        }

        protected void ProcessConnect(Socket socket, object state, SocketAsyncEventArgs e, Exception exception)
        {
            if (exception != null)
            {
                m_InConnecting = false;
                OnError(exception);

                if (e != null)
                    e.Dispose();
                ConnectTimeout.Set();
                return;
            }

            if (e != null && e.SocketError != SocketError.Success)
            {
                m_InConnecting = false;
                OnError(new SocketException((int)e.SocketError));
                e.Dispose();
                ConnectTimeout.Set();
                return;
            }

            if (socket == null)
            {
                m_InConnecting = false;
                OnError(new SocketException((int)SocketError.ConnectionAborted));
                ConnectTimeout.Set();
                return;
            }

            //To walk around a MonoTouch's issue
            //one user reported in some cases the e.SocketError = SocketError.Succes but the socket is not connected in MonoTouch
            if (!socket.Connected)
            {
                m_InConnecting = false;
                ConnectTimeout.Set();
                var socketError = SocketError.HostUnreachable;

#if !SILVERLIGHT && !NETFX_CORE
                try
                {
                    socketError = (SocketError)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                }
                catch (Exception ex)
                {
                    socketError = SocketError.HostUnreachable;
                    EE.LogError("GetSocketOption Error", ex,log);
                }                
#endif
                OnError(new SocketException((int)socketError));
                return;
            }

            if (e == null)
                e = new SocketAsyncEventArgs();

            e.Completed += SocketEventArgsCompleted;

            Client = socket;
            m_InConnecting = false;

#if !SILVERLIGHT
            try
            {
                // mono may throw an exception here
                LocalEndPoint = socket.LocalEndPoint;
            }
            catch(Exception ex)
            {
                EE.LogError("Get socket.LocalEndPoint Error", ex, log);
            }
#endif

            var finalRemoteEndPoint = e.RemoteEndPoint != null ? e.RemoteEndPoint : socket.RemoteEndPoint;

            if (string.IsNullOrEmpty(HostName))
            {
                HostName = GetHostOfEndPoint(finalRemoteEndPoint);
            }
            else// connect with DnsEndPoint
            {
                var finalDnsEndPoint = finalRemoteEndPoint as DnsEndPoint;

                if (finalDnsEndPoint != null)
                {
                    var hostName = finalDnsEndPoint.Host;

                    if (!string.IsNullOrEmpty(hostName) && !HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                        HostName = hostName;
                }
            }

#if !SILVERLIGHT && !NETFX_CORE
            try
            {
                //Set keep alive
                Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                //byte[] inValue = new byte[] { 1, 0, 0, 0, 0x20, 0x4e, 0, 0, 0xd0, 0x07, 0, 0 };// 首次探测时间20 秒, 间隔侦测时间2 秒
                byte[] inValue = new byte[] { 1, 0, 0, 0, 0x88, 0x13, 0, 0, 0xd0, 0x07, 0, 0 };// 首次探测时间5 秒, 间隔侦测时间2 秒
                Client.IOControl(IOControlCode.KeepAliveValues, inValue, null);
            }
            catch(Exception ex)
            {
                EE.LogError("SetSocketOption Error", ex, log);
            }
            ConnectTimeout.Set();
#endif
            OnGetSocket(e);
        }

        /// 当socket.connected为false时，进一步确定下当前连接状态
        /// 
        /// 
        public override bool IsSocketConnected()
        {
            #region remarks
            /********************************************************************************************
             * 当Socket.Conneted为false时， 如果您需要确定连接的当前状态，请进行非阻塞、零字节的 Send 调用。
             * 如果该调用成功返回或引发 WAEWOULDBLOCK 错误代码 (10035)，则该套接字仍然处于连接状态； 
             * 否则，该套接字不再处于连接状态。
             * Depending on http://msdn.microsoft.com/zh-cn/library/system.net.sockets.socket.connected.aspx?cs-save-lang=1&cs-lang=csharp#code-snippet-2
            ********************************************************************************************/
            #endregion

            #region 过程
            if (Client == null)
            {
                return false;
            }
            // This is how you can determine whether a socket is still connected.
            bool connectState = true;
            bool blockingState = Client.Blocking;
            try
            {
                byte[] tmp = new byte[1];

                Client.Blocking = false;
                Client.Send(tmp, 0, 0);
                //Console.WriteLine("Connected!");
                connectState = true; //若Send错误会跳去执行catch体，而不会执行其try体里其之后的代码
            }
            catch (SocketException e)
            {
                // 10035 == WSAEWOULDBLOCK
                if (e.NativeErrorCode.Equals(10035))
                {
                    //Console.WriteLine("Still Connected, but the Send would block");
                    connectState = true;
                }
                else
                {
                    //Console.WriteLine("Disconnected: error code {0}!", e.NativeErrorCode);
                    connectState = false;
                }
            }
            finally
            {
                Client.Blocking = blockingState;
            }
            //Console.WriteLine("Connected: {0}", client.Connected);
            return connectState;
            #endregion
        }

        ///

        /// 另一种判断connected的方法，但未检测对端网线断开或ungraceful的情况
        /// 
        /// 
        /// 
        public static bool IsSocketConnected(Socket s)
        {
            #region remarks
            /* As zendar wrote, it is nice to use the Socket.Poll and Socket.Available, but you need to take into consideration 
             * that the socket might not have been initialized in the first place. 
             * This is the last (I believe) piece of information and it is supplied by the Socket.Connected property. 
             * The revised version of the method would looks something like this: 
             * from：http://stackoverflow.com/questions/2661764/how-to-check-if-a-socket-is-connected-disconnected-in-c */
            #endregion

            #region 过程

            if (s == null)
                return false;
            return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);

            /* The long, but simpler-to-understand version:
                    bool part1 = s.Poll(1000, SelectMode.SelectRead);
                    bool part2 = (s.Available == 0);
                    if ((part1 && part2 ) || !s.Connected)
                        return false;
                    else
                        return true;
            */
            #endregion
        }
        private string GetHostOfEndPoint(EndPoint endPoint)
        {
            var dnsEndPoint = endPoint as DnsEndPoint;

            if (dnsEndPoint != null)
            {
                return dnsEndPoint.Host;
            }

            var ipEndPoint = endPoint as IPEndPoint;

            if (ipEndPoint != null && ipEndPoint.Address != null)
               return ipEndPoint.Address.ToString();

            return string.Empty;
        }

        protected abstract void OnGetSocket(SocketAsyncEventArgs e);

        protected bool EnsureSocketClosed()
        {
            return EnsureSocketClosed(null);
        }

        protected bool EnsureSocketClosed(Socket prevClient)
        {
            var client = Client;

            if (client == null)
                return false;

            var fireOnClosedEvent = true;

            if (prevClient != null && prevClient != client)//originalClient is previous disconnected socket, so we needn't fire event for it
            {
                client = prevClient;
                fireOnClosedEvent = false;
            }
            else
            {
                Client = null;
                m_IsSending = 0;
            }

            try
            {
                client.Shutdown(SocketShutdown.Both);
            }
            catch
            {}
            finally
            {
                try
                {
#if NETFX_CORE                  
                    client.Dispose();
#else
                    client.Close();
#endif
                }
                catch
                {}
            }

            return fireOnClosedEvent;
        }

        private bool DetectConnected()
        {
            if (Client != null)
                return true;
            OnError(new SocketException((int)SocketError.NotConnected));
            return false;
        }

        private IBatchQueue<ArraySegment<byte>> m_SendingQueue;

        private IBatchQueue<ArraySegment<byte>> GetSendingQueue()
        {
            if (m_SendingQueue != null)
                return m_SendingQueue;

            lock (this)
            {
                if (m_SendingQueue != null)
                    return m_SendingQueue;

                //Sending queue size must be greater than 3
                m_SendingQueue = new ConcurrentBatchQueue<ArraySegment<byte>>(Math.Max(SendingQueueSize, 1024), (t) => t.Array == null || t.Count == 0);
                return m_SendingQueue;
            }
        }

        private PosList<ArraySegment<byte>> m_SendingItems;

        private PosList<ArraySegment<byte>> GetSendingItems()
        {
            if (m_SendingItems == null)
                m_SendingItems = new PosList<ArraySegment<byte>>();

            return m_SendingItems;
        }

        private int m_IsSending = 0;

        protected bool IsSending
        {
            get { return m_IsSending == 1; }
        }

        public override bool TrySend(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count == 0)
            {
                log.Error("Send Data Error:The data to be sent cannot be empty.");
                //throw new Exception("The data to be sent cannot be empty.");
                return true;
            }

            if (!DetectConnected())
            {
                //may be return false? 
                return true;
            }

            var isEnqueued = GetSendingQueue().Enqueue(segment);

            if (Interlocked.CompareExchange(ref m_IsSending, 1, 0) != 0)
                return isEnqueued;

            DequeueSend();

            return isEnqueued;
        }

        public override bool TrySend(IList<ArraySegment<byte>> segments)
        {
            if (segments == null || segments.Count == 0)
            {
                log.Error("Send Data Error:segments sent cannot be empty.");
                // throw new ArgumentNullException("segments");
                return true;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                
                if (seg.Count == 0)
                {
                    log.Error("Send Data Error:The data piece to be sent cannot be empty.");
                    //throw new Exception("The data piece to be sent cannot be empty.");
                    return true;
                }
            }

            if (!DetectConnected())
            {
                //may be return false? 
                return true;
            }

            var isEnqueued = GetSendingQueue().Enqueue(segments);

            if (Interlocked.CompareExchange(ref m_IsSending, 1, 0) != 0)
                return isEnqueued;

            DequeueSend();

            return isEnqueued;
        }

        private void DequeueSend()
        {
            var sendingItems = GetSendingItems();

            if (!m_SendingQueue.TryDequeue(sendingItems))
            {
                m_IsSending = 0;
                return;
            }

            SendInternal(sendingItems);
        }

        protected abstract void SendInternal(PosList<ArraySegment<byte>> items);

        protected void OnSendingCompleted()
        {
            var sendingItems = GetSendingItems();
            sendingItems.Clear();
            sendingItems.Position = 0;

            if (!m_SendingQueue.TryDequeue(sendingItems))
            {
                m_IsSending = 0;
                return;
            }

            SendInternal(sendingItems);
        }

        public override void Close()
        {
            if (EnsureSocketClosed())
                OnClosed();
        }

        /// <summary>
        /// 断线重连函数
        /// </summary>
        /// <returns></returns>
        public override bool Reconnect()
        {
            try
            {
                Close();
                Connect(m_RemoteEndPoint);
                ConnectTimeout.Reset();
                if (ConnectTimeout.WaitOne(10000, false))//直到timeout，或者TimeoutObject.se
                {
                    return IsConnected;
                }               
            }
            catch (Exception ex)
            {
                EE.LogError("Reconnect Error", ex, log);
            }
            return false;
        }
    }
}
