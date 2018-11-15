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
using System.Net;
using System.Threading;

namespace Cave.Net
{
    /// <summary>
    /// Provides a TcpServer interface
    /// </summary>
    /// <seealso cref="IDisposable" />
    public interface ITcpServer : IDisposable
    {
        /// <summary>Listens at the specified end point.</summary>
        /// <param name="endPoint">The end point.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        void Listen(IPEndPoint endPoint);

        /// <summary>Listens at the specified port.</summary>
        /// <param name="port">The port.</param>
        /// <exception cref="System.ObjectDisposedException">TcpSocketServer</exception>
        void Listen(int port);

        /// <summary>Closes the server and performs shutdown on all clients.</summary>
        void Close();

        /// <summary>Gets or sets the maximum length of the pending connections queue.</summary>
        /// <value>The maximum length of the pending connections queue.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        int AcceptBacklog { get; set; }

        /// <summary>Gets or sets the size of the buffer used when receiving data.</summary>
        /// <value>The size of the buffer.</value>
        /// <exception cref="System.InvalidOperationException">Socket is already listening!</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">value</exception>
        int BufferSize { get; set; }

        /// <summary>Gets or sets the amount of time, in milliseconds, thata read operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a read operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the read operation does not time out.</value>
        int ReceiveTimeout { get; set; }

        /// <summary>Gets or sets the amount of time, in milliseconds, thata write operation blocks waiting for data.</summary>
        /// <value>A Int32 that specifies the amount of time, in milliseconds, that will elapse before a write operation fails. The default value, <see cref="Timeout.Infinite"/>, specifies that the write operation does not time out.</value>
        int SendTimeout { get; set; }

        /// <summary>Gets a value indicating whether this instance is listening.</summary>
        /// <value>
        /// <c>true</c> if this instance is listening; otherwise, <c>false</c>.
        /// </value>
        bool IsListening { get; }
    }
}
