using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cave.IO;
#if NETSTANDARD13 || NETSTANDARD20 || NET45 || NET46 || NET47
using System.Threading.Tasks;
#endif

namespace Cave.Net
{
    /// <summary>
    /// Provides an async tcp client implementation
    /// </summary>
    [DebuggerDisplay("{RemoteEndPoint}")]
    public class TcpAsyncClient : IDisposable
    {
        class AsyncParameters
        {
            public AsyncParameters(Socket socket, int bufferSize)
            {
                Socket = socket;
                BufferSize = bufferSize;
            }

            public Socket Socket { get; }

            public int BufferSize { get; }
        }

        #region private class
        bool connectedEventTriggered;
        bool closing;
        bool initialized;
        bool disconnectedEventTriggered;
        long bytesReceived;
        long bytesSent;
        SocketAsyncEventArgs socketAsync;
        int receiveTimeout;
        int sendTimeout;
        short ttl;
        bool nodelay;
        LingerOption lingerState;

        /// <summary>Provides access to the TCP socket.</summary>
        /// <value>The TCP socket instance.</value>
        Socket socket;

        Socket Socket
        {
            get
            {
                if (socket == null)
                {
                    throw new InvalidOperationException("Not connected!");
                }
                if (closing)
                {
                    throw new ObjectDisposedException(nameof(TcpAsyncClient));
                }
                return socket;
            }
        }

        T CachedValue<T>(ref T field, Func<T> func)
        {
            if (socket != null && !closing)
            {
                field = func();
            }
            return field;
        }

        Socket CreateSocket()
        {
            if (closing)
            {
                throw new ObjectDisposedException(nameof(TcpAsyncClient));
            }
#if NETSTANDARD13 || NET20 || NET35
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
#else
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
#endif
            socket.ExclusiveAddressUse = false;
            socket.LingerState = new LingerOption(false, 0);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
            return socket;
        }

        /// <summary>Gets called whenever a read is completed.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="SocketAsyncEventArgs"/> instance containing the event data.</param>
        void ReadCompleted(object sender, SocketAsyncEventArgs e)
        {
ReadCompletedBegin:
            if (closing)
            {
                return;
            }

            var bytesTransferred = e.BytesTransferred;

            switch (e.SocketError)
            {
                case SocketError.Success:
                    break;
                case SocketError.ConnectionReset:
                    Close();
                    return;
                default:
                    OnError(new SocketException((int)e.SocketError));
                    Close();
                    return;
            }

            try
            {
                // got data (if not this is the disconnected call)
                if (bytesTransferred > 0)
                {
                    Interlocked.Add(ref bytesReceived, bytesTransferred);

                    // call event
                    var bufferEventArgs = new BufferEventArgs(e.Buffer, e.Offset, bytesTransferred);
                    OnReceived(bufferEventArgs);
                    if (!bufferEventArgs.Handled)
                    {
                        // cleanup read buffers and add new data
                        lock (ReceiveBuffer)
                        {
                            ReceiveBuffer?.FreeBuffers();
                            ReceiveBuffer?.AppendBuffer(bufferEventArgs.Buffer, bufferEventArgs.Offset, bufferEventArgs.Length);
                            OnBuffered();
                            Monitor.PulseAll(ReceiveBuffer);
                        }
                    }

                    // still connected after event ?
                    if (IsConnected)
                    {
                        // yes read again
                        if (!Socket.ReceiveAsync(socketAsync))
                        {
                            e = socketAsync;
                            goto ReadCompletedBegin;

                            // we could do a function call to myself here but with slow OnReceived() functions and fast networks we might get a stack overflow caused by infinite recursion
                            // spawning threads using the threadpool is not a good idea either, because multiple receives will mess up our (sequential) stream reading.
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
            Close();
        }

        void InitializeClient(Socket socket)
        {
            if (initialized)
            {
                throw new InvalidOperationException("Already initialized!");
            }
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
            Stream = new TcpAsyncStream(this);
            initialized = true;
        }

        #endregion

        #region internal functions used by TcpSocketServer

        /// <summary>
        /// Initializes the client for use with the specified <paramref name="server"/> instance.
        /// </summary>
        /// <exception cref="InvalidOperationException">Reader already started!</exception>
        /// <param name="server">Server instance this client belongs to.</param>
        /// <param name="socket">Socket instance this client uses.</param>
        protected internal virtual void InitializeServer(ITcpServer server, Socket socket)
        {
            if (initialized)
            {
                throw new InvalidOperationException("Already initialized!");
            }
            Server = server ?? throw new ArgumentNullException(nameof(server));
            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
            socket.ReceiveTimeout = server.ReceiveTimeout;
            socket.SendTimeout = server.SendTimeout;
            socket.LingerState = new LingerOption(false, 0);
            InitializeClient(socket);
        }

        /// <summary>
        /// Calls the <see cref="OnConnect()"/> function and starts the async socket reader.
        /// </summary>
        /// <param name="bufferSize">The <see cref="Socket.SendBufferSize"/> and <see cref="Socket.ReceiveBufferSize"/> to be used.</param>
        internal void StartReader(int bufferSize)
        {
            if (socketAsync != null)
            {
                throw new InvalidOperationException("Reader already started!");
            }
            OnConnect();
            Socket.SendBufferSize = bufferSize;
            Socket.ReceiveBufferSize = bufferSize;
            var buffer = new byte[bufferSize];
            socketAsync = new SocketAsyncEventArgs() { UserToken = this, };
            socketAsync.Completed += ReadCompleted;
            socketAsync.SetBuffer(buffer, 0, buffer.Length);
            if (!Socket.ReceiveAsync(socketAsync))
            {
#if NET20 || NET35
                ThreadPool.QueueUserWorkItem(delegate
                {
                    ReadCompleted(this, socketAsync);
                });
#else
                Task.Factory.StartNew(() =>
                {
                    ReadCompleted(this, socketAsync);
                });
#endif
            }
        }

        #endregion

        #region events

        /// <summary>
        /// Calls the <see cref="Connected"/> event (if set).
        /// </summary>
        protected virtual void OnConnect()
        {
            if (connectedEventTriggered)
            {
                throw new InvalidOperationException("OnConnect triggered twice!");
            }

            connectedEventTriggered = true;
            Connected?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Calls the <see cref="Disconnected"/> event (if set).
        /// </summary>
        protected virtual void OnDisconnect()
        {
            if (connectedEventTriggered && !disconnectedEventTriggered)
            {
                disconnectedEventTriggered = true;
                Disconnected?.Invoke(this, new EventArgs());
            }
        }

        /// <summary>
        /// Calls the <see cref="Received"/> event (if set).
        /// </summary>
        /// <remarks>
        /// You can set <see cref="BufferEventArgs.Handled"/> to true when overrideing this function or within <see cref="Received"/>
        /// to skip adding data to the <see cref="Stream"/> and <see cref="ReceiveBuffer"/>.
        /// </remarks>
        /// <param name="e">The buffer event arguments.</param>
        protected virtual void OnReceived(BufferEventArgs e)
        {
            Received?.Invoke(this, e);
        }

        /// <summary>
        /// Calls the <see cref="Buffered"/> event (if set).
        /// </summary>
        protected virtual void OnBuffered()
        {
            Buffered?.Invoke(this, new EventArgs());
        }

        /// <summary>Calls the Error event (if set) and closes the connection.</summary>
        /// <param name="ex">The exception (in most cases this will be a <see cref="SocketException"/></param>
        protected internal virtual void OnError(Exception ex)
        {
            if (!closing)
            {
                Error?.Invoke(this, new ExceptionEventArgs(ex));
                Close();
            }
        }

        /// <summary>
        /// Event to be called after the connection was established
        /// </summary>
        public event EventHandler<EventArgs> Connected;

        /// <summary>
        /// Event to be called after the connection was closed
        /// </summary>
        public event EventHandler<EventArgs> Disconnected;

        /// <summary>
        /// Event to be called after a buffer was received
        /// </summary>
        public event EventHandler<BufferEventArgs> Received;

        /// <summary>
        /// Event to be called after a buffer was received and was not handled by the <see cref="Received"/> event
        /// </summary>
        public event EventHandler<EventArgs> Buffered;

        /// <summary>
        /// Event to be called after an error was encountered
        /// </summary>
        public event EventHandler<ExceptionEventArgs> Error;

        #endregion

        #region public functions

#if NETSTANDARD13
        void Connect(AsyncParameters parameters, Task task)
        {
            try
            {
                task.Wait(ConnectTimeout);
                if (task.IsFaulted)
                {
                    throw task.Exception;
                }

                if (task.IsCompleted)
                {
                    InitializeClient(parameters.Socket);
                    StartReader(parameters.BufferSize);
                    return;
                }
                Close();
                throw new TimeoutException();
            }
            catch (Exception ex)
            {
                OnError(ex);
                throw;
            }
        }
#else
        void Connect(AsyncParameters parameters, IAsyncResult asyncResult)
        {
            try
            {
                if (asyncResult.AsyncWaitHandle.WaitOne(ConnectTimeout))
                {
                    parameters.Socket.EndConnect(asyncResult);
                    InitializeClient(parameters.Socket);
                    StartReader(parameters.BufferSize);
                }
                else
                {
                    Close();
                    throw new TimeoutException();
                }
            }
            catch (Exception ex)
            {
                OnError(ex);
                throw;
            }
        }
#endif

        void ConnectAsyncCallback(object sender, SocketAsyncEventArgs e)
        {
            // try everything to avoid throwing an expensive exception in async mode
            try
            {
                if (e.SocketError != SocketError.Success)
                {
#if !NET20 && !NET35
                    if (e.ConnectByNameError != null)
                    {
                        OnError(e.ConnectByNameError);
                        return;
                    }
#endif
                    OnError(new SocketException((int)e.SocketError));
                    return;
                }
                if (!Socket.Connected)
                {
                    OnError(new SocketException((int)SocketError.SocketError));
                    return;
                }
                try
                {
                    var parameters = (AsyncParameters)e.UserToken;
                    InitializeClient(parameters.Socket);
                    StartReader(parameters.BufferSize);
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
            }
            finally
            {
                e.Dispose();
            }
        }

#if NET20 || NET35
        void ConnectAsyncCallback(IAsyncResult asyncResult)
        {
            // try everything to avoid throwing an expensive exception in async mode
            try
            {
                if (!Socket.Connected)
                {
                    OnError(new SocketException((int)SocketError.SocketError));
                    return;
                }
                Socket.EndConnect(asyncResult);
                InitializeClient(socket);
                StartReader(((AsyncParameters)asyncResult.AsyncState).BufferSize);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }
#endif

        /// <summary>
        /// Connects to the specified hostname and port
        /// </summary>
        /// <param name="hostname">hostname to resolve</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(string hostname, int port, int bufferSize = 64 * 1024)
        {
            if (closing)
            {
                throw new ObjectDisposedException(nameof(TcpAsyncClient));
            }

            if (initialized)
            {
                throw new InvalidOperationException("Client already connected!");
            }
            var socket = CreateSocket();
            var parameters = new AsyncParameters(socket, bufferSize);
#if NETSTANDARD13
            Connect(parameters, socket.ConnectAsync(hostname, port));
#else
            Connect(parameters, socket.BeginConnect(hostname, port, null, null));
#endif
        }

        /// <summary>
        /// Connects to the specified hostname and port
        /// </summary>
        /// <param name="hostname">hostname to resolve</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void ConnectAsync(string hostname, int port, int bufferSize = 64 * 1024)
        {
            if (closing)
            {
                throw new ObjectDisposedException(nameof(TcpAsyncClient));
            }

            if (initialized)
            {
                throw new InvalidOperationException("Client already connected!");
            }

#if NET20 || NET35
            var socket = CreateSocket();
            Socket.BeginConnect(hostname, port, ConnectAsyncCallback, new AsyncParameters(socket, bufferSize));
#else
            ConnectAsync(new DnsEndPoint(hostname, port), bufferSize);
#endif
        }

        /// <summary>
        /// Connects to the specified address and port
        /// </summary>
        /// <param name="address">ip address to connect to</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(IPAddress address, int port, int bufferSize = 64 * 1024)
        {
            if (closing)
            {
                throw new ObjectDisposedException(nameof(TcpAsyncClient));
            }

            if (initialized)
            {
                throw new InvalidOperationException("Client already connected!");
            }

            RemoteEndPoint = new IPEndPoint(address, port);
            var socket = CreateSocket();
            var parameters = new AsyncParameters(socket, bufferSize);
#if NETSTANDARD13
            Connect(parameters, socket.ConnectAsync(address, port));
#else
            Connect(parameters, socket.BeginConnect(address, port, null, null));
#endif
        }

        /// <summary>
        /// Performs an asynchonous connect to the specified address and port
        /// </summary>
        /// <remarks>
        /// This function returns immediately.
        /// Results are delivered by the <see cref="Error"/> / <see cref="Connected"/> events.
        /// </remarks>
        /// <param name="address">ip address to connect to</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void ConnectAsync(IPAddress address, int port, int bufferSize = 64 * 1024)
        {
            if (closing)
            {
                throw new ObjectDisposedException(nameof(TcpAsyncClient));
            }

            if (initialized)
            {
                throw new InvalidOperationException("Client already connected!");
            }

            RemoteEndPoint = new IPEndPoint(address, port);
            ConnectAsync(RemoteEndPoint, bufferSize);
        }

        /// <summary>
        /// Connects to the specified address and port
        /// </summary>
        /// <param name="endPoint">ip endpoint to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(IPEndPoint endPoint, int bufferSize = 64 * 1024)
        {
            Connect(endPoint.Address, endPoint.Port, bufferSize);
        }

        /// <summary>
        /// Performs an asynchonous connect to the specified address and port
        /// </summary>
        /// <remarks>
        /// This function returns immediately.
        /// Results are delivered by the <see cref="Error"/> / <see cref="Connected"/> events.
        /// </remarks>
        /// <param name="endPoint">ip endpoint to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void ConnectAsync(EndPoint endPoint, int bufferSize = 64 * 1024)
        {
            RemoteEndPoint = endPoint as IPEndPoint;
            var socket = CreateSocket();
            var e = new SocketAsyncEventArgs()
            {
                RemoteEndPoint = endPoint,
                UserToken = new AsyncParameters(socket, bufferSize),
            };
            e.Completed += ConnectAsyncCallback;
            if (!socket.ConnectAsync(e))
            {
                ConnectAsyncCallback(socket, e);
            }
        }

        /// <summary>Gets the stream.</summary>
        /// <returns>Returns the <see cref="Stream"/> instance used to send and receive data.</returns>
        /// <remarks>This function and access to all stream functions are threadsafe.</remarks>
        /// <exception cref="System.InvalidOperationException">Not connected!</exception>
        public virtual Stream GetStream() => Stream;

        /// <summary>
        /// Sends data to a connected remote.
        /// </summary>
        /// <remarks>This function is threadsafe.</remarks>
        /// <param name="buffer">An array of bytes to be send.</param>
        public void Send(byte[] buffer) => Send(buffer, 0, buffer.Length);

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <remarks>This function is threadsafe</remarks>
        /// <param name="buffer">An array of bytes to be send.</param>
        /// <param name="length">The number of bytes.</param>
        public void Send(byte[] buffer, int length) => Send(buffer, 0, length);

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <remarks>This function is threadsafe</remarks>
        /// <param name="buffer">An array of bytes to be send.</param>
        /// <param name="offset">The start offset at the byte array.</param>
        /// <param name="length">The number of bytes.</param>
        public void Send(byte[] buffer, int offset, int length)
        {
            lock (this)
            {
                if (!IsConnected)
                {
                    throw new InvalidOperationException("Not connected!");
                }
                try
                {
                    Socket.Send(buffer, offset, length, 0);
                }
                catch (Exception ex)
                {
                    OnError(ex);
                    Close();
                    throw;
                }
                Interlocked.Add(ref bytesSent, length - offset);
            }
        }

        /// <summary>Closes this instance gracefully.</summary>
        /// <remarks>This function is threadsafe</remarks>
        public virtual void Close()
        {
            lock (this)
            {
                if (closing)
                {
                    return;
                }

                closing = true;
                OnDisconnect();

                if (socket != null)
                {
                    try
                    {
                        socket.Shutdown(SocketShutdown.Both);
#if !NETSTANDARD13
                        socket.Close();
#endif
                    }
                    catch { }
                }
            }
            DisposeUnmanaged();
        }
        #endregion

        #region IDisposable Support

        void DisposeUnmanaged()
        {
            if (socket is IDisposable disposable)
            {
                disposable.Dispose();
            }
            socket = null;
            socketAsync?.Dispose();
            socketAsync = null;
        }

        /// <summary>Releases the unmanaged resources used by this instance and optionally releases the managed resources.</summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            closing = true;
            DisposeUnmanaged();
            Stream?.Dispose();
            Stream = null;
            ReceiveBuffer = null;
        }

        /// <summary>Releases unmanaged and managed resources.</summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
        #endregion

        #region public properties

        /// <summary>Gets the raw TCP stream used to send and receive data.</summary>
        /// <remarks>This function and access to all stream functions are threadsafe</remarks>
        /// <value>The TCP stream instance.</value>
        public TcpAsyncStream Stream { get; private set; }

        /// <summary>Gets the receive buffer.</summary>
        /// <value>The receive buffer.</value>
        /// <remarks>Use lock on this buffer to ensure thread safety when using concurrent access to the <see cref="Stream"/> property, <see cref="GetStream()"/> function and/or <see cref="Received"/> callbacks.</remarks>
        public FifoStream ReceiveBuffer { get; private set; } = new FifoStream();

        /// <summary>Gets or sets the amount of time, in milliseconds, that a connect operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the connect operation does not time out.</value>
        public int ConnectTimeout { get; set; } = 5000;

        /// <summary>Gets a value indicating whether the client is connected.</summary>
        public bool IsConnected => !closing && (socket?.Connected).GetValueOrDefault();

        /// <summary>Gets the number of bytes received.</summary>
        public long BytesReceived => Interlocked.Read(ref bytesReceived);

        /// <summary>Gets the number of bytes sent.</summary>
        public long BytesSent => Interlocked.Read(ref bytesSent);

        /// <summary>Gets the remote end point.</summary>
        /// <value>The remote end point.</value>
        public IPEndPoint RemoteEndPoint { get; private set; }

        /// <summary>Gets the local end point.</summary>
        /// <value>The local end point.</value>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>Gets or sets the amount of time, in milliseconds, that a read operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the read operation does not time out.</value>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public int ReceiveTimeout
        {
            get => CachedValue(ref receiveTimeout, () => socket.ReceiveTimeout);
            set => Socket.ReceiveTimeout = value;
        }

        /// <summary>Gets or sets the amount of time, in milliseconds, that a write operation blocks waiting for transmission.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public int SendTimeout
        {
            get => CachedValue(ref sendTimeout, () => socket.SendTimeout);
            set => Socket.SendTimeout = value;
        }

        /// <summary>
        /// Gets or sets a value that specifies the Time To Live (TTL) value of Internet Protocol (IP) packets sent by the Socket.
        /// </summary>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public short Ttl
        {
            get => CachedValue(ref ttl, () => socket.Ttl);
            set => Socket.Ttl = value;
        }

        /// <summary>Gets or sets a value indicating whether the stream Socket is using the Nagle algorithm.</summary>
        /// <value><c>true</c> if the Socket uses the Nagle algorithm; otherwise, <c>false</c>.</value>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public bool NoDelay
        {
            get => CachedValue(ref nodelay, () => socket.NoDelay);
            set => Socket.NoDelay = value;
        }

        /// <summary>
        /// Gets or sets a value that specifies whether the Socket will delay closing a socket in an attempt to send all pending data.
        /// </summary>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public LingerOption LingerState
        {
            get => CachedValue(ref lingerState, () => socket.LingerState);
            set => Socket.LingerState = value;
        }

        /// <summary>
        /// Gets the server instance this client belongs to. May be <c>null</c>.
        /// </summary>
        public ITcpServer Server { get; private set; }
        #endregion

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>tcp://remoteip:port</returns>
        public override string ToString()
        {
            return $"tcp://{RemoteEndPoint}";
        }
    }
}
