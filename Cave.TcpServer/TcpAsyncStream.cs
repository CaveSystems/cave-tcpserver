using System;
using System.IO;
using System.Threading;
using Cave.IO;

namespace Cave.Net
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
        public override bool CanRead => true;

        /// <summary>
        /// Returns false
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// Returns true
        /// </summary>
        public override bool CanWrite => true;

        /// <summary>
        /// Returns the number of bytes received (<see cref="TcpAsyncClient.BytesReceived"/>)
        /// </summary>
        public override long Length => m_Client.BytesReceived;

        /// <summary>
        /// Returns the current read position at the buffers still present in memory
        /// </summary>
        public override long Position
        {
            get => m_Client.ReceiveBuffer.Position;
            set => throw new NotSupportedException();
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
            DateTime timeout = m_Client.ReceiveTimeout > 0 ? DateTime.UtcNow + TimeSpan.FromMilliseconds(m_Client.ReceiveTimeout) : DateTime.MaxValue;
            FifoStream buffer = m_Client.ReceiveBuffer;
            lock (buffer)
            {
                while (true)
                {
                    if (m_Exit) { return 0; }
                    if (!m_Client.IsConnected) { throw new EndOfStreamException(); }
                    if (buffer.Available > 0) { break; }
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

#if NETSTANDARD13
        /// <summary>
        /// Closes the tcp connection
        /// </summary>
        public virtual void Close()
        {
            if (!m_Exit)
            {
                if (m_Client.IsConnected) { m_Client.Close(); }
                m_Exit = true;
            }
        }
#else
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
#endif
    }
}