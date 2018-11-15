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

using Cave.IO;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cave.Net
{
    /// <summary>
    /// Provides an async tcp client implementation
    /// </summary>
    public class TcpAsyncClient : EventBase, IDisposable
    {
		#region private class
		volatile bool m_Closing;
		long m_BytesReceived;
		long m_BytesSent;
		SocketAsyncEventArgs m_SocketAsync;
		int m_ReceiveTimeout = Timeout.Infinite;
		int m_SendTimeout = Timeout.Infinite;

        /// <summary>Gets called whenever a read is completed.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="SocketAsyncEventArgs"/> instance containing the event data.</param>
        void ReadCompleted(object sender, SocketAsyncEventArgs e)
        {
        ReadCompletedBegin:
            if (m_Closing)
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
                    OnError(new ExceptionEventArgs(new SocketException((int)e.SocketError)));
                    Dispose();
                    return;
            }

            try
            {
                //got data (if not this is the disconnected call)
                if (bytesTransferred > 0)
                {
                    Interlocked.Add(ref m_BytesReceived, bytesTransferred);
                    //call event
                    var bufferEventArgs = new BufferEventArgs(e.Buffer, e.Offset, bytesTransferred);
                    OnReceived(bufferEventArgs);
                    if (!bufferEventArgs.Handled)
                    {
                        //cleanup read buffers and add new data
                        lock (ReceiveBuffer)
                        {
                            ReceiveBuffer?.FreeBuffers();
                            ReceiveBuffer?.AppendBuffer(e.Buffer, e.Offset, bytesTransferred);
                            OnBuffered(new EventArgs());
                            Monitor.PulseAll(ReceiveBuffer);
                        }
                    }
                    //still connected after event ?
                    if (IsConnected)
                    {
                        //yes read again
                        if (!Socket.ReceiveAsync(m_SocketAsync))
                        {
                            e = m_SocketAsync;
                            goto ReadCompletedBegin;
                            //we could do a function call to myself here but with slow OnReceived() functions and fast networks we might get a stack overflow caused by infinite recursion
                            //spawning threads using the threadpool is not a good idea either, because multiple receives will mess up our (sequential) stream reading.
                        }
                        return;
                    }
                }
                OnDisconnect(new EventArgs());
            }
            catch (Exception ex)
            {
                OnError(new ExceptionEventArgs(ex));
            }
            Socket?.Close();
        }
		#endregion

        #region internal functions used by TcpSocketServer

        /// <summary>
        /// Initializes the client.
        /// Calls the <see cref="OnConnect(EventArgs)"/> function and starts the async socket reader.
        /// </summary>
		/// <exception cref="InvalidOperationException">Reader already started!</exception>
        internal void Initialize(ITcpServer server, Socket socket, int bufferSize)
        {
            if (Socket != null) { throw new InvalidOperationException("Already initialized!"); }
            if (socket == null) { throw new ArgumentNullException("socket"); }

            RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;

            Server = server;
            Socket = socket;
            Socket.ReceiveTimeout = ReceiveTimeout;
            Socket.SendTimeout = SendTimeout;

            Stream = new TcpAsyncStream(this)
            {
                ReadTimeout = ReceiveTimeout,
                WriteTimeout = SendTimeout
            };

            OnConnect(new EventArgs());
            if (m_SocketAsync != null) { throw new InvalidOperationException("Reader already started!"); }
            byte[] buffer = new byte[bufferSize];
            m_SocketAsync = new SocketAsyncEventArgs() { UserToken = this, };
            m_SocketAsync.Completed += ReadCompleted;
            m_SocketAsync.SetBuffer(buffer, 0, buffer.Length);
            if (!Socket.ReceiveAsync(m_SocketAsync))
            {
                Task.Factory.StartNew(delegate
                {
                    ReadCompleted(this, m_SocketAsync);
                });
            }
        }
        #endregion

        #region events

        /// <summary>
        /// Calls the <see cref="Connected"/> event (if set).
        /// </summary>
        protected virtual void OnConnect(EventArgs e)
        {
            CallEvent(Connected, e, true);
        }

        /// <summary>
        /// Calls the <see cref="Disconnected"/> event (if set).
        /// </summary>
        protected virtual void OnDisconnect(EventArgs e)
        {
            CallEvent(Disconnected, e, true);
        }

        /// <summary>
        /// Calls the <see cref="Received"/> event (if set).
        /// </summary>
        protected virtual void OnReceived(BufferEventArgs e)
		{
			CallEvent(Received, e);
		}

        /// <summary>
        /// Calls the <see cref="Buffered"/> event (if set).
        /// </summary>
        protected virtual void OnBuffered(EventArgs e)
        {
            CallEvent(Buffered, e);
        }

        /// <summary>Closes the connection and calls the Error event (if set).</summary>
        /// <param name="e"></param>
        protected override void OnError(ExceptionEventArgs e)
		{
			if (!m_Closing)
			{
                Close();
                base.OnError(e);
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

        #endregion

        #region public functions

        void Connect(Socket socket, int bufferSize, IAsyncResult asyncResult)
        {
            if (asyncResult.AsyncWaitHandle.WaitOne(ConnectTimeout))
            {
                socket.EndConnect(asyncResult);
                Initialize(null, socket, bufferSize);
            }
            else
            {
                socket.Close();
                throw new TimeoutException();
            }
        }

        /// <summary>
        /// Connects to the specified hostname and port
        /// </summary>
        /// <param name="hostname">hostname to resolve</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(string hostname, int port, int bufferSize = 64 * 1024)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = false
            };
            Connect(socket, bufferSize, socket.BeginConnect(hostname, port, null, null));
        }        

        /// <summary>
        /// Connects to the specified address and port
        /// </summary>
        /// <param name="address">ip address to connect to</param>
        /// <param name="port">port to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(IPAddress address, int port, int bufferSize = 64 * 1024)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = false
            };
            Connect(socket, bufferSize, socket.BeginConnect(address, port, null, null));
        }

        /// <summary>
        /// Connects to the specified address and port
        /// </summary>
        /// <param name="endPoint">ip endpoint to connect to</param>
        /// <param name="bufferSize">tcp buffer size in bytes</param>
        public void Connect(IPEndPoint endPoint, int bufferSize = 64 * 1024)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = false
            };
            Connect(socket, bufferSize, socket.BeginConnect(endPoint, null, null));
        }

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
            Socket.Send(buffer);
            Interlocked.Add(ref m_BytesSent, buffer.Length);
        }

        /// <summary>
        /// Sends data to a connected remote
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        public void Send(byte[] buffer, int length)
        {
            if (!IsConnected) { throw new InvalidOperationException("Not connected!"); }
            Socket.Send(buffer, 0, length, 0);
            Interlocked.Add(ref m_BytesSent, length);
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
            Socket.Send(buffer, offset, length, 0);
            Interlocked.Add(ref m_BytesSent, length - offset);
        }

        /// <summary>Closes this instance.</summary>
        public void Close() => Dispose();
        #endregion

        #region IDisposable Support

        /// <summary>Releases the unmanaged resources used by this instance and optionally releases the managed resources.</summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
		{
            if (m_Closing)
            {
                return;
            }

            m_Closing = true;
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
            Socket?.Close();
            Stream?.Dispose();
            m_SocketAsync?.Dispose();
            m_SocketAsync = null;
            Stream = null;
            Socket = null;
            ReceiveBuffer = null;
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

        /// <summary>Provides access to the TCP socket.</summary>
        /// <value>The TCP socket instance.</value>
        Socket Socket;

        /// <summary>Gets the receive buffer.</summary>
        /// <value>The receive buffer.</value>
        public FifoStream ReceiveBuffer { get; private set; } = new FifoStream();

        /// <summary>Gets or sets the amount of time, in milliseconds, that a connect operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the connect operation does not time out.</value>
        public int ConnectTimeout { get; set; } = 5000;

        /// <summary>Gets or sets the amount of time, in milliseconds, that a read operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the read operation does not time out.</value>
        public int ReceiveTimeout
		{
			get => m_ReceiveTimeout; 
			set
			{
				if (Socket == null) { m_ReceiveTimeout = value; }
				else { m_ReceiveTimeout = Socket.ReceiveTimeout = value; }
			}
		}

		/// <summary>Gets or sets the amount of time, in milliseconds, that a write operation blocks waiting for transmission.</summary>
		/// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
		public int SendTimeout
		{
			get => m_SendTimeout; 
			set
			{
				if (Socket == null) { m_SendTimeout = value; }
				else { m_SendTimeout = Socket.SendTimeout = value; }
			}
		}

        /// <summary>Gets a value indicating whether the client is connected.</summary>
        public bool IsConnected => (Socket?.Connected).GetValueOrDefault();

        /// <summary>Gets the number of bytes received.</summary>
        public long BytesReceived => Interlocked.Read(ref m_BytesReceived);

        /// <summary>Gets the number of bytes sent.</summary>
        public long BytesSent => Interlocked.Read(ref m_BytesSent);

        /// <summary>Gets the remote end point.</summary>
        /// <value>The remote end point.</value>
        public IPEndPoint RemoteEndPoint { get; private set; }

        /// <summary>Gets the local end point.</summary>
        /// <value>The local end point.</value>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>Gets or sets a Boolean value that specifies whether the stream Socket is using the Nagle algorithm.</summary>
        /// <value><c>true</c> if the Socket uses the Nagle algorithm; otherwise, <c>false</c>.</value>
        public bool NoDelay { get => Socket.NoDelay; set => Socket.NoDelay = value; }

        /// <summary>
        /// Server instance this client belongs to. May be <c>null</c>.
        /// </summary>
        public ITcpServer Server { get; private set; }
		#endregion
	}
}