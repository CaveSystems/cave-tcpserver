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
using System.Net.Sockets;
using System.Threading;

namespace Cave
{
    /// <summary>
    /// Provides a stream implementation for <see cref="TcpAsyncClient"/>
    /// </summary>
    public class TcpAsyncStream : Stream
    {
        TcpAsyncClient m_Client;
        bool m_Exit;

        /// <summary>
        /// Creates a new instance of the <see cref="TcpAsyncStream"/> class
        /// </summary>
        /// <param name="client"></param>
        public TcpAsyncStream(TcpAsyncClient client)
        {
            m_Client = client;
        }

        /// <summary>
        /// Number of bytes available for reading
        /// </summary>
        public int Available { get { lock (m_Client) { return m_Client.ReceiveBuffer.Available; } } }

        /// <summary>Gets or sets the amount of time, in milliseconds, that a read operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the read operation does not time out.</value>
        public override int ReadTimeout { get => m_Client.ReceiveTimeout; set => m_Client.ReceiveTimeout = value; }

        /// <summary>Gets or sets the amount of time, in milliseconds, that a write operation blocks waiting for transmission.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
        public override int WriteTimeout { get => m_Client.SendTimeout; set => m_Client.SendTimeout = value; }

        /// <summary>
        /// Returns true
        /// </summary>
        public override bool CanRead { get { return true; } }

        /// <summary>
        /// Returns false
        /// </summary>
        public override bool CanSeek { get { return false; } }

        /// <summary>
        /// Returns true
        /// </summary>
        public override bool CanWrite { get { return true; } }

        /// <summary>
        /// Returns the number of bytes received (<see cref="TcpAsyncClient.BytesReceived"/>)
        /// </summary>
        public override long Length { get { return m_Client.BytesReceived; } }

        /// <summary>
        /// Returns the current read position at the buffers still present in memory
        /// </summary>
        public override long Position
        {
            get { return m_Client.ReceiveBuffer.Position; }
            set { throw new NotSupportedException(); }
        }

        /// <summary>
        /// Does nothing
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// Reads data from the the buffers. A maximum of count bytes is read but if less is available any number of bytes may be read.
        /// If no bytes are available the read method will block until at least one byte is available, the connection is closed or the timeout is reached.
        /// </summary>
        /// <param name="array">byte array to write data to</param>
        /// <param name="offset">start offset at array to begin writing at</param>
        /// <param name="count">number of bytes to read</param>
        /// <returns></returns>
        public override int Read(byte[] array, int offset, int count)
        {
            bool blockMessageSent = false;
            DateTime timeout = m_Client.ReceiveTimeout > 0 ? DateTime.UtcNow + TimeSpan.FromMilliseconds(m_Client.ReceiveTimeout) : DateTime.MaxValue;
            FifoStream buffer = m_Client.ReceiveBuffer;
            lock (buffer)
            {
                while (true)
                {
                    if (m_Exit) { return 0; }
                    if (!m_Client.IsConnected) { throw new EndOfStreamException(); }
                    if (buffer.Available > 0) { break; }
                    if (!blockMessageSent)
                    {
                        Trace.TraceWarning("Blocking thread and waiting for incoming data ({0}/{1} bytes)", count, buffer.Available);
                        blockMessageSent = true;
                    }
                    int waitTime = (int)Math.Min(1000, (timeout - DateTime.UtcNow).Ticks / TimeSpan.TicksPerMillisecond);
                    if (waitTime <= 0) { throw new TimeoutException(); }
                    Monitor.Wait(buffer, waitTime);
                }
                return buffer.Read(array, offset, count);
            }
        }

        /// <summary>
        /// Not supported
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="origin"></param>
        /// <returns></returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Not supported
        /// </summary>
        /// <param name="value"></param>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Writes data to the tcp connection
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (m_Exit) { return; }
            m_Client.Send(buffer, offset, count);
        }

        /// <summary>
        /// Closes the tcp connection
        /// </summary>
        public override void Close()
        {
            if (!m_Exit)
            {
                base.Close();
                if (m_Client.IsConnected) { m_Client.Close(); }
                m_Exit = true;
            }
        }
    }
}