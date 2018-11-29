#region CopyRight 2018
/*
    Copyright (c) 2012-2018 Andreas Rohleder (andreas@rohleder.cc)
    All rights reserved
*/
#endregion
#region License LGPL-3
/*
    This program/library/sourcecode is free software; you can redistribute it
    and/or modify it under the terms of the GNU Lesser General Public License
    version 3 as published by the Free Software Foundation subsequent called
    the License.

    You may not use this program/library/sourcecode except in compliance
    with the License. The License is included in the LICENSE file
    found at the installation directory or the distribution package.

    Permission is hereby granted, free of charge, to any person obtaining
    a copy of this software and associated documentation files (the
    "Software"), to deal in the Software without restriction, including
    without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to
    permit persons to whom the Software is furnished to do so, subject to
    the following conditions:

    The above copyright notice and this permission notice shall be included
    in all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
    LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
    OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
    WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion
#region Authors & Contributors
/*
   Author:
     Andreas Rohleder <andreas@rohleder.cc>

   Contributors:
 */
#endregion

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
            public AsyncParameters(int bufferSize) => BufferSize = bufferSize;
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

        /// <summary>Provides access to the TCP socket.</summary>
        /// <value>The TCP socket instance.</value>
        Socket socket;

        Socket Socket
        {
            get
            {
                if (socket == null)
                {
                    if (closing) throw new ObjectDisposedException(nameof(TcpAsyncClient));
#if NETSTANDARD13
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        ExclusiveAddressUse = false
                    };
#elif NET20 || NET35
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        ExclusiveAddressUse = false
                    };
#else
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        ExclusiveAddressUse = false
                    };
#endif
                }
                return socket;
            }
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

            int bytesTransferred = e.BytesTransferred;

            switch (e.SocketError)
            {
                case SocketError.Success: break;
                case SocketError.ConnectionReset:
                    Close();
                    return;
                default:
                    OnError(new SocketException((int)e.SocketError));
                    Dispose();
                    return;
            }

            try
            {
                //got data (if not this is the disconnected call)
                if (bytesTransferred > 0)
                {
                    Interlocked.Add(ref bytesReceived, bytesTransferred);
                    //call event
                    var bufferEventArgs = new BufferEventArgs(e.Buffer, e.Offset, bytesTransferred);
                    OnReceived(e.Buffer, e.Offset, bytesTransferred);
                    if (!bufferEventArgs.Handled)
                    {
                        //cleanup read buffers and add new data
                        lock (ReceiveBuffer)
                        {
                            ReceiveBuffer?.FreeBuffers();
                            ReceiveBuffer?.AppendBuffer(e.Buffer, e.Offset, bytesTransferred);
                            OnBuffered();
                            Monitor.PulseAll(ReceiveBuffer);
                        }
                    }
                    //still connected after event ?
                    if (IsConnected)
                    {
                        //yes read again
                        if (!Socket.ReceiveAsync(socketAsync))
                        {
                            e = socketAsync;
                            goto ReadCompletedBegin;
                            //we could do a function call to myself here but with slow OnReceived() functions and fast networks we might get a stack overflow caused by infinite recursion
                            //spawning threads using the threadpool is not a good idea either, because multiple receives will mess up our (sequential) stream reading.
                        }
                        return;
                    }
                }
                OnDisconnect();
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
#if NETSTANDARD13
            Socket?.Dispose();
#else
            Socket?.Close();
#endif
        }

        void InitializeClient()
        {
            if (initialized)
            {
                throw new InvalidOperationException("Already initialized!");
            }
            RemoteEndPoint = (IPEndPoint)Socket.RemoteEndPoint;
            LocalEndPoint = (IPEndPoint)Socket.LocalEndPoint;
            Stream = new TcpAsyncStream(this);
            initialized = true;
        }

        #endregion

        #region internal functions used by TcpSocketServer

        /// <summary>
        /// Initializes the client.
        /// </summary>
        /// <exception cref="InvalidOperationException">Reader already started!</exception>
        internal void InitializeServer(ITcpServer server, Socket socket)
        {
            if (initialized)
            {
                throw new InvalidOperationException("Already initialized!");
            }
            Server = server ?? throw new ArgumentNullException(nameof(server));
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            if (server != null)
            {
                socket.ReceiveTimeout = server.ReceiveTimeout;
                socket.SendTimeout = server.SendTimeout;
            }
            InitializeClient();
        }

        /// <summary>
        /// Calls the <see cref="OnConnect()"/> function and starts the async socket reader.
        /// </summary>
        /// <param name="bufferSize"></param>
        internal void StartReader(int bufferSize)
        {
            if (socketAsync != null) { throw new InvalidOperationException("Reader already started!"); }
            OnConnect();
            Socket.SendBufferSize = bufferSize;
            Socket.SendBufferSize = bufferSize;
            byte[] buffer = new byte[bufferSize];
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
                Task.Factory.StartNew(delegate
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
            if (connectedEventTriggered) throw new InvalidOperationException("OnConnect triggered twice!");
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
        protected virtual void OnReceived(byte[] buffer, int offset, int length)
        {
            Received?.Invoke(this, new BufferEventArgs(buffer, offset, length));
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
        protected virtual void OnError(Exception ex)
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
        void Connect(int bufferSize, Task task)
        {
            try
            {
                task.Wait(ConnectTimeout);
                if (task.IsFaulted) throw task.Exception;
                if (task.IsCompleted)
                {
                    InitializeClient();
                    StartReader(bufferSize);
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
        void Connect(int bufferSize, IAsyncResult asyncResult)
        {
            try
            {
                if (asyncResult.AsyncWaitHandle.WaitOne(ConnectTimeout))
                {
                    Socket.EndConnect(asyncResult);
                    InitializeClient();
                    StartReader(bufferSize);
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
                InitializeClient();
                StartReader(((AsyncParameters)e.UserToken).BufferSize);
            }
            catch (Exception ex)
            {
                OnError(ex);
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
                InitializeClient();
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
            if (closing) throw new ObjectDisposedException(nameof(TcpAsyncClient));
            if (initialized) throw new InvalidOperationException("Client already connected!");
#if NETSTANDARD13
            Connect(bufferSize, Socket.ConnectAsync(hostname, port));
#else
            Connect(bufferSize, Socket.BeginConnect(hostname, port, null, null));
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
            if (closing) throw new ObjectDisposedException(nameof(TcpAsyncClient));
            if (initialized) throw new InvalidOperationException("Client already connected!");

#if NET20 || NET35
            Socket.BeginConnect(hostname, port, ConnectAsyncCallback, new AsyncParameters(bufferSize));
#else
            SocketAsyncEventArgs e = new SocketAsyncEventArgs()
            {
                RemoteEndPoint = new DnsEndPoint(hostname, port),
                UserToken = new AsyncParameters(bufferSize),
            };
            e.Completed += ConnectAsyncCallback;
            Socket.ConnectAsync(e);
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
            if (closing) throw new ObjectDisposedException(nameof(TcpAsyncClient));
            if (initialized) throw new InvalidOperationException("Client already connected!");
            RemoteEndPoint = new IPEndPoint(address, port);
#if NETSTANDARD13
            Connect(bufferSize, Socket.ConnectAsync(address, port));
#else
            Connect(bufferSize, Socket.BeginConnect(address, port, null, null));
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
            if (closing) throw new ObjectDisposedException(nameof(TcpAsyncClient));
            if (initialized) throw new InvalidOperationException("Client already connected!");
            RemoteEndPoint = new IPEndPoint(address, port);
            SocketAsyncEventArgs e = new SocketAsyncEventArgs()
            {
                RemoteEndPoint = new IPEndPoint(address, port),
                UserToken = new AsyncParameters(bufferSize),
            };
            e.Completed += ConnectAsyncCallback;
            Socket.ConnectAsync(e);
        }

        /// <summary>
        /// Connects to the specified address and port
        /// </summary>
        /// <param name="endPoint">ip endpoint to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(IPEndPoint endPoint, int bufferSize = 64 * 1024) => Connect(endPoint.Address, endPoint.Port, bufferSize);

        /// <summary>
        /// Performs an asynchonous connect to the specified address and port
        /// </summary>
        /// <remarks>
        /// This function returns immediately. 
        /// Results are delivered by the <see cref="Error"/> / <see cref="Connected"/> events.
        /// </remarks>
        /// <param name="endPoint">ip endpoint to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void ConnectAsync(IPEndPoint endPoint, int bufferSize = 64 * 1024) => ConnectAsync(endPoint.Address, endPoint.Port, bufferSize);

        /// <summary>Gets the stream.</summary>
        /// <returns></returns>
        /// <exception cref="System.InvalidOperationException">Not connected!</exception>
        public virtual Stream GetStream()
        {
            if (!IsConnected) { throw new InvalidOperationException("Not connected!"); }
            return Stream;
        }

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <param name="buffer"></param>
        public void Send(byte[] buffer)
        {
            if (!IsConnected) { throw new InvalidOperationException("Not connected!"); }
            try
            {
                Socket.Send(buffer);
            }
            catch
            {
                Close();
                throw;
            }
            Interlocked.Add(ref bytesSent, buffer.Length);
        }

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        public void Send(byte[] buffer, int length)
        {
            if (!IsConnected) { throw new InvalidOperationException("Not connected!"); }
            try
            {
                Socket.Send(buffer, 0, length, 0);
            }
            catch
            {
                Close();
                throw;
            }
            Interlocked.Add(ref bytesSent, length);
        }

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        public void Send(byte[] buffer, int offset, int length)
        {
            if (!IsConnected) { throw new InvalidOperationException("Not connected!"); }
            try
            {
                Socket.Send(buffer, offset, length, 0);
            }
            catch
            {
                Close();
                throw;
            }
            Interlocked.Add(ref bytesSent, length - offset);
        }

        /// <summary>Closes this instance.</summary>
        public void Close() => Dispose();
        #endregion

        #region IDisposable Support

        /// <summary>Releases the unmanaged resources used by this instance and optionally releases the managed resources.</summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (closing)
            {
                return;
            }

            closing = true;
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
#if NETSTANDARD13
            socket?.Dispose();
#else
            socket?.Close();
#endif
            socket = null;
            Stream?.Dispose();
            socketAsync?.Dispose();
            socketAsync = null;
            Stream = null;
            ReceiveBuffer = null;
            OnDisconnect();
        }

        /// <summary>Releases unmanaged and managed resources.</summary>
        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

        #region public properties

        /// <summary>Provides access to the raw TCP stream.</summary>
        /// <value>The TCP stream instance.</value>
        public TcpAsyncStream Stream { get; private set; }

        /// <summary>Gets the receive buffer.</summary>
        /// <value>The receive buffer.</value>
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
            get => Socket.ReceiveTimeout;
            set => Socket.ReceiveTimeout = value;
        }

        /// <summary>Gets or sets the amount of time, in milliseconds, that a write operation blocks waiting for transmission.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public int SendTimeout
        {
            get => Socket.SendTimeout;
            set => Socket.SendTimeout = value;
        }

        /// <summary>
        /// Gets or sets a value that specifies the Time To Live (TTL) value of Internet Protocol (IP) packets sent by the Socket.
        /// </summary>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public short Ttl
        {
            get => Socket.Ttl;
            set => Socket.Ttl = value;
        }

        /// <summary>Gets or sets a Boolean value that specifies whether the stream Socket is using the Nagle algorithm.</summary>
        /// <value><c>true</c> if the Socket uses the Nagle algorithm; otherwise, <c>false</c>.</value>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public bool NoDelay
        {
            get => Socket.NoDelay;
            set => Socket.NoDelay = value;
        }

        /// <summary>
        /// Gets or sets a value that specifies whether the Socket will delay closing a socket in an attempt to send all pending data.
        /// </summary>
        /// <remarks>This cannot be accessed prior <see cref="Connect(string, int, int)"/></remarks>
        public LingerOption LingerState
        {
            get => Socket.LingerState;
            set => Socket.LingerState = value;
        }

        /// <summary>
        /// Server instance this client belongs to. May be <c>null</c>.
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

    /// <summary>
    /// Provides an async tcp client implementation for typed server instances
    /// </summary>
    /// <typeparam name="TServer"></typeparam>
    public class TcpAsyncClient<TServer> : TcpAsyncClient
        where TServer : ITcpServer
    {
        /// <summary>
        /// Server instance this client belongs to. May be <c>null</c>.
        /// </summary>
        public new TServer Server => (TServer)base.Server;
    }
}
