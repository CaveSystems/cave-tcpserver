using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cave.Net
{
    /// <summary>
    /// Provides a fast TcpServer implementation
    /// </summary>
    /// <typeparam name="TClient">The type of the client.</typeparam>
    /// <seealso cref="IDisposable" />
    [ComVisible(false)]
    public class TcpServer<TClient> : ITcpServer where TClient : TcpAsyncClient, new()
    {
#if NETSTANDARD13 || NET20
        readonly Dictionary<SocketAsyncEventArgs, SocketAsyncEventArgs> m_PendingAccepts = new Dictionary<SocketAsyncEventArgs, SocketAsyncEventArgs>();
        readonly Dictionary<TClient, TClient> m_Clients = new Dictionary<TClient, TClient>();

        void AddPendingAccept(SocketAsyncEventArgs e)
        {
            m_PendingAccepts.Add(e, e);
        }

        void AddClient(TClient client)
        {
            m_Clients.Add(client, client);
        }

        void RemoveClient(TClient client) { if (m_Clients.ContainsKey(client)) { m_Clients.Remove(client); } }
        IEnumerable<TClient> ClientList => m_Clients.Keys;
        IEnumerable<SocketAsyncEventArgs> PendingAcceptList => m_PendingAccepts.Keys;
#else
        readonly HashSet<SocketAsyncEventArgs> m_PendingAccepts = new HashSet<SocketAsyncEventArgs>();
        readonly HashSet<TClient> m_Clients = new HashSet<TClient>();

        void AddPendingAccept(SocketAsyncEventArgs e)
        {
            m_PendingAccepts.Add(e);
        }

        void AddClient(TClient client)
        {
            m_Clients.Add(client);
        }

        void RemoveClient(TClient client) { if (m_Clients.Contains(client)) { m_Clients.Remove(client); } }
        IEnumerable<TClient> ClientList => m_Clients;
        IEnumerable<SocketAsyncEventArgs> PendingAcceptList => m_PendingAccepts;
#endif

        Socket m_Socket;
        int m_AcceptBacklog = 20;
        int m_AcceptThreads = 2;
        int m_TcpBufferSize = 64 * 1024;
        bool m_Shutdown;
        int m_AcceptWaiting;

        void AcceptStart()
        {
            while (true)
            {
                SocketAsyncEventArgs asyncAccept;
                lock (m_PendingAccepts)
                {
                    if (m_PendingAccepts.Count >= AcceptThreads)
                    {
                        return;
                    }

                    asyncAccept = new SocketAsyncEventArgs();
                    AddPendingAccept(asyncAccept);
                }

                //accept async or sync, call AcceptCompleted in any case
                Interlocked.Increment(ref m_AcceptWaiting);
                asyncAccept.Completed += AcceptCompleted;
                if (!m_Socket.AcceptAsync(asyncAccept))
                {
#if NET20 || NET35
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        AcceptCompleted(this, asyncAccept);
                    });
#else
                    System.Threading.Tasks.Task.Factory.StartNew(delegate
                    {
                        AcceptCompleted(this, asyncAccept);
                    });
#endif
                }
            }
        }

        void AcceptCompleted(object sender, SocketAsyncEventArgs e)
        {
            AcceptCompletedBegin:
            int waiting = Interlocked.Decrement(ref m_AcceptWaiting);
            if (waiting == 0)
            {
                OnAcceptTasksBusy();
            }
            //handle accepted socket
            {
                Socket socket = e.AcceptSocket;
                while (socket != null && socket.Connected)
                {
                    //create client
                    TClient client = new TClient();
                    client.Disconnected += ClientDisconnected;
                    try
                    {
                        //add to my client list
                        lock (m_Clients) { AddClient(client); }
                        //initialize client instance with server and socket
                        client.InitializeServer(this, socket);
                        //call client accepted event
                        OnClientAccepted(client);
                        //start processing of incoming data
                        client.StartReader(BufferSize);
                    }
                    catch (Exception ex)
                    {
#if NETSTANDARD13
                        socket.Dispose();
#else
                        socket.Close();
#endif
                        socket = null;
                        OnClientException(client, ex);
                        break;
                    }
                    break;
                }
            }
            //start next socket accept
            if (!m_Shutdown)
            {
                //accept next
                Interlocked.Increment(ref m_AcceptWaiting);
                e.AcceptSocket = null;
                if (!m_Socket.AcceptAsync(e))
                {
                    //AcceptCompleted(this, e);
                    goto AcceptCompletedBegin;
                    //we could do a function call to myself here but with slow OnClientAccepted() functions and fast networks we might get a stack overflow caused by infinite recursion
                }
            }
        }

        void ClientDisconnected(object sender, EventArgs e)
        {
            //perform cleanup of client list
            TClient client = (TClient)sender;
            lock (m_Clients)
            {
                RemoveClient(client);
            }
        }

        /// <summary>
        /// Calls the <see cref="ClientException"/> event (if set).
        /// </summary>
        protected virtual void OnClientException(TClient client, Exception ex)
        {
            ClientException?.Invoke(this, new TcpServerClientExceptionEventArgs<TClient>(client, ex));
        }

        /// <summary>
        /// Calls the <see cref="AcceptTasksBusy"/> event (if set).
        /// </summary>
        protected virtual void OnAcceptTasksBusy()
        {
            AcceptTasksBusy?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Calls the <see cref="ClientAccepted"/> event (if set).
        /// </summary>
        protected virtual void OnClientAccepted(TClient client)
        {
            ClientAccepted?.Invoke(this, new TcpServerClientEventArgs<TClient>(client));
        }

        /// <summary>Initializes a new instance of the <see cref="TcpServer{TClient}"/> class.</summary>
        public TcpServer() { }

        /// <summary>Listens at the specified end point.</summary>
        /// <param name="endPoint">The end point.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        public void Listen(IPEndPoint endPoint)
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException("TcpSocketServer");
            }

            m_Socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            switch (endPoint.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    break;
                case AddressFamily.InterNetworkV6:
                    //Patches for net 2.0: SocketOptionLevel 41 = IPv6   SocketOptionName 27 = IPv6Only
                    m_Socket.SetSocketOption((SocketOptionLevel)41, (SocketOptionName)27, false);
                    break;
            }

            //m_Socket.UseOnlyOverlappedIO = true;
            m_Socket.Bind(endPoint);
            m_Socket.Listen(AcceptBacklog);
            LocalEndPoint = (IPEndPoint)m_Socket.LocalEndPoint;
            AcceptStart();
        }


        /// <summary>Listens at the specified port.</summary>
        /// <param name="port">The port.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        public void Listen(int port)
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException("TcpSocketServer");
            }
#if NETSTANDARD13
            Listen(new IPEndPoint(IPAddress.Any, port));
#else
            bool useIPv6 = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6));
            if (useIPv6)
            {
                Listen(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            else
            {
                Listen(new IPEndPoint(IPAddress.Any, port));
            }
#endif
        }

        /// <summary>Disconnects all clients.</summary>
        public void DisconnectAllClients()
        {
            lock (m_Clients)
            {
                foreach (TClient c in ClientList)
                {
                    c.Close();
                }
                m_Clients.Clear();
            }
        }

        /// <summary>Closes this instance.</summary>
        public void Close()
        {
            m_Shutdown = true;
            lock (m_PendingAccepts)
            {
                foreach (SocketAsyncEventArgs e in PendingAcceptList)
                {
                    e.Dispose();
                }
                m_PendingAccepts.Clear();

                if (m_Socket != null)
                {
#if NETSTANDARD13
                    m_Socket.Dispose();
#else
                    m_Socket.Close();
#endif
                    m_Socket = null;
                }
            }

            DisconnectAllClients();
            Dispose();
        }

        /// <summary>Gets or sets the maximum number of pending connections.</summary>
        /// <value>The maximum length of the pending connections queue.</value>
        /// <remarks>On high load this should be 10 x <see cref="AcceptThreads"/></remarks>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        public int AcceptBacklog
        {
            get => m_AcceptBacklog;
            set
            {
                if (m_Socket != null)
                {
                    throw new InvalidOperationException("Socket is already listening!");
                }

                m_AcceptBacklog = Math.Max(1, value);
            }
        }

        /// <summary>Gets or sets the number of threads used to accept connections.</summary>
        /// <value>The maximum length of the pending connections queue.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        public int AcceptThreads
        {
            get => m_AcceptThreads;
            set
            {
                if (m_Socket != null)
                {
                    throw new InvalidOperationException("Socket is already listening!");
                }

                m_AcceptThreads = Math.Max(1, value);
            }
        }

        /// <summary>Gets or sets the size of the buffer used when receiving data.</summary>
        /// <value>The size of the buffer.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">value</exception>
        public int BufferSize
        {
            get => m_TcpBufferSize;
            set
            {
                if (m_Socket != null)
                {
                    throw new InvalidOperationException("Socket is already listening!");
                }

                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                m_TcpBufferSize = value;
            }
        }

        /// <summary>Gets or sets the amount of time, in milliseconds, thata read operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the read operation does not time out.</value>
        public int ReceiveTimeout { get; set; } = Timeout.Infinite;

        /// <summary>Gets or sets the amount of time, in milliseconds, thata write operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
        public int SendTimeout { get; set; } = Timeout.Infinite;

        /// <summary>Gets the local end point.</summary>
        /// <value>The local end point.</value>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>Gets a value indicating whether this instance is listening.</summary>
        /// <value>
        /// <c>true</c> if this instance is listening; otherwise, <c>false</c>.
        /// </value>
        public bool IsListening => m_Socket != null && m_Socket.IsBound;

        /// <summary>
        /// Event to be called whenever all accept tasks get busy. This may indicate declined connections attempts (due to a full backlog).
        /// </summary>
        public event EventHandler<EventArgs> AcceptTasksBusy;

        /// <summary>
        /// Event to be called after a client was accepted occured
        /// </summary>
        public event EventHandler<TcpServerClientEventArgs<TClient>> ClientAccepted;

        /// <summary>
        /// Event to be called after a client exception occured that cannot be handled by the clients Error event.
        /// </summary>
        public event EventHandler<TcpServerClientExceptionEventArgs<TClient>> ClientException;

        /// <summary>Gets all connected clients.</summary>
        /// <value>The clients.</value>
        public TClient[] Clients
        {
            get
            {
                lock (m_Clients)
                {
                    return ClientList.ToArray();
                }
            }
        }

        #region IDisposable Support
        bool m_Disposed = false;

        /// <summary>Releases the unmanaged resources used by this instance and optionally releases the managed resources.</summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!m_Disposed)
            {
                if (m_Socket != null)
                {
                    (m_Socket as IDisposable)?.Dispose();
                    m_Socket = null;
                }
                m_Disposed = true;
            }
        }

        /// <summary>Releases unmanaged and managed resources.</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>tcp://localip:port</returns>
        public override string ToString()
        {
            return $"tcp://{LocalEndPoint}";
        }
    }

    /// <summary>
    /// Provides a fast TcpServer implementation using the default TcpServerClient class.
    /// For own client implementations use <see cref="TcpServer{TcpServerClient}"/>
    /// </summary>
    /// <seealso cref="IDisposable" />
    [ComVisible(false)]
    public class TcpServer : TcpServer<TcpAsyncClient>
    { }
}
