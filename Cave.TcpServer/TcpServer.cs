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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cave
{
	/// <summary>
	/// Provides a fast TcpServer implementation
	/// </summary>
	/// <typeparam name="TClient">The type of the client.</typeparam>
	/// <seealso cref="IDisposable" />
	[ComVisible(false)]
    public class TcpServer<TClient> : EventBase, ITcpServer where TClient : TcpAsyncClient, new()
    {
        readonly HashSet<SocketAsyncEventArgs> m_AcceptPending = new HashSet<SocketAsyncEventArgs>();
        readonly HashSet<TClient> m_Clients = new HashSet<TClient>();
        Socket m_Socket;
        int m_AcceptBacklog = 20;
        int m_AcceptThreads = 2;
        int m_TcpBufferSize = 64 * 1024;
		bool m_Shutdown;
        int m_AcceptWaiting;

        void ClientsCleanup()
        {
            foreach (TClient c in m_Clients.ToArray())
            {
                if (!c.IsConnected) m_Clients.Remove(c);
            }
        }

        void AcceptStart()
        {
            while (true)
            {
                SocketAsyncEventArgs asyncAccept;
                lock (m_AcceptPending)
                {
                    if (m_AcceptPending.Count >= AcceptThreads) return;
                    asyncAccept = new SocketAsyncEventArgs();
                    m_AcceptPending.Add(asyncAccept);
                    Trace.TraceInformation("New async accept task. ({0}/{1})", m_AcceptPending.Count, m_AcceptThreads);
                }

                //accept async or sync, call AcceptCompleted in any case
                Interlocked.Increment(ref m_AcceptWaiting);
                asyncAccept.Completed += AcceptCompleted;
                if (!m_Socket.AcceptAsync(asyncAccept))
                {
                    Task.Factory.StartNew(delegate
                    {
                        AcceptCompleted(this, asyncAccept);
                    });
                }
            }
        }

        void AcceptCompleted(object sender, SocketAsyncEventArgs e)
        {
            AcceptCompletedBegin:
            var waiting = Interlocked.Decrement(ref m_AcceptWaiting);
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
                    try
                    {
                        //add to my client list
                        lock (m_Clients) { m_Clients.Add(client); }
                        client.Initialize(this, socket, BufferSize);
                        //call client accepted event
                        OnClientAccepted(client);
                        lock (m_Clients) { ClientsCleanup(); }
                    }
                    catch (Exception ex)
                    {
                        try { socket.Close(); } catch { }
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

        protected virtual void OnClientException(TClient client, Exception ex)
        {
            CallEvent(ClientException, new TcpServerClientExceptionEventArgs<TClient>(client, ex));
        }

        /// <summary>
        /// Calls the <see cref="AcceptTasksBusy"/> event (if set).
        /// </summary>
        protected virtual void OnAcceptTasksBusy()
        {
            CallEvent(AcceptTasksBusy, new EventArgs());
        }

        /// <summary>
        /// Calls the <see cref="ClientAccepted"/> event (if set).
        /// </summary>
        protected virtual void OnClientAccepted(TClient client)
		{
            CallEvent(ClientAccepted, new TcpServerClientEventArgs<TClient>(client));
		}

        /// <summary>Initializes a new instance of the <see cref="TcpServer{TClient}"/> class.</summary>
        public TcpServer() { }

        /// <summary>Listens at the specified end point.</summary>
        /// <param name="endPoint">The end point.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        public void Listen(IPEndPoint endPoint)
        {
            if (m_Disposed) throw new ObjectDisposedException("TcpSocketServer");
            m_Socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            switch(endPoint.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    break;
                case AddressFamily.InterNetworkV6:
                    //Patches for net 2.0: SocketOptionLevel 41 = IPv6   SocketOptionName 27 = IPv6Only
                    m_Socket.SetSocketOption((SocketOptionLevel)41, (SocketOptionName)27, false);
                    break;
            }

            //m_Socket.UseOnlyOverlappedIO = true;
            Trace.TraceInformation("Start listening at <cyan>{0}<default>.", endPoint);
            m_Socket.Bind(endPoint);
            m_Socket.Listen(AcceptBacklog);
			LocalEndPoint = (IPEndPoint)m_Socket.LocalEndPoint;
            Trace.TraceInformation("Listening using {0} asynchronous accept calls and a backlog of {1} connections.", AcceptThreads, AcceptBacklog);
            AcceptStart();
        }


        /// <summary>Listens at the specified port.</summary>
        /// <param name="port">The port.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        public void Listen(int port)
        {
            if (m_Disposed) throw new ObjectDisposedException("TcpSocketServer");
            var localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses);
            bool useIPv6 = localAddresses.Any(i => i.Address.AddressFamily == AddressFamily.InterNetworkV6);
			if (useIPv6)
			{
				Trace.TraceInformation("<green>IPv6 <default>usage detected, trying to open socket for IPv4 and IPv6...");
                Listen(new IPEndPoint(IPAddress.IPv6Any, port));
            }
			else
			{
                Listen(new IPEndPoint(IPAddress.Any, port));
            }
        }

		/// <summary>Disconnects all clients.</summary>
		public void DisconnectAllClients()
		{
            Trace.TraceInformation("Disconnect all clients.");
			lock (m_Clients)
			{
				foreach (TClient c in m_Clients)
				{
					c.Dispose();
				}
				m_Clients.Clear();
			}
		}

        /// <summary>Closes this instance.</summary>
        public void Close()
        {
			m_Shutdown = true;
            Trace.TraceInformation("Shutdown server.");
			lock (m_AcceptPending)
            {
                foreach (SocketAsyncEventArgs e in m_AcceptPending)
                {
                    e.Dispose();
                }
                m_AcceptPending.Clear();

				if (m_Socket != null)
				{
					try { m_Socket.Close(); } catch { }
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
            get { return m_AcceptBacklog; }
            set
            {
                if (m_Socket != null) throw new InvalidOperationException("Socket is already listening!");
                m_AcceptBacklog = Math.Max(1, value);
            }
        }

        /// <summary>Gets or sets the number of threads used to accept connections.</summary>
        /// <value>The maximum length of the pending connections queue.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        public int AcceptThreads
        {
            get { return m_AcceptThreads; }
            set
            {
                if (m_Socket != null) throw new InvalidOperationException("Socket is already listening!");
                m_AcceptThreads = Math.Max(1, value);
            }
        }

        /// <summary>Gets or sets the size of the buffer used when receiving data.</summary>
        /// <value>The size of the buffer.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">value</exception>
        public int BufferSize
        {
            get { return m_TcpBufferSize; }
            set
            {
                if (m_Socket != null) throw new InvalidOperationException("Socket is already listening!");
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
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
        public bool IsListening { get { return m_Socket != null && m_Socket.IsBound; } }

        /// <summary>
        /// Event to be called whenever all accept tasks get busy. This may indicate declined connections attempts (due to a full backlog).
        /// </summary>
        public event EventHandler<EventArgs> AcceptTasksBusy;

        /// <summary>
        /// Event to be called after a client was accepted occured
        /// </summary>
        public event EventHandler<TcpServerClientEventArgs<TClient>> ClientAccepted;

        /// <summary>
        /// Event to be called after a client exception occured that cannot be handled by the client instance (<see cref="TClient.Error"/>)
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
                    ClientsCleanup();
                    return m_Clients.ToArray();
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
    }

    /// <summary>
    /// Provides a fast TcpServer implementation using the default TcpServerClient class.
    /// For own client implementations use <see cref="TcpServer{TcpServerClient}"/>
    /// </summary>
    /// <seealso cref="Cave.EventBase" />
    /// <seealso cref="System.IDisposable" />
    [ComVisible(false)]
    public class TcpServer : TcpServer<TcpAsyncClient>
    { }
}
