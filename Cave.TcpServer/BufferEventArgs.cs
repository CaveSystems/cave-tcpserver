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

namespace Cave.Net
{
    /// <summary>
    /// Provides buffer information
    /// </summary>
    public class BufferEventArgs : EventArgs
    {
        bool handled;

        /// <summary>
        /// Full buffer instance
        /// </summary>
        public byte[] Buffer { get; }

        /// <summary>
        /// Start offset of data in <see cref="Buffer"/>
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// Length of data in <see cref="Buffer"/>
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Set this to true to tell the sender that the buffer has been handled. Further processing will be skipped.
        /// </summary>
        public bool Handled { get => handled; set => handled |= value; }

        /// <summary>
        /// Creates a new <see cref="BufferEventArgs"/> instance
        /// </summary>
        /// <param name="buffer">buffer instance</param>
        /// <param name="offset">offset of data</param>
        /// <param name="length">length of data</param>
        public BufferEventArgs(byte[] buffer, int offset, int length)
        {
            Buffer = buffer;
            Offset = offset;
            Length = length;
        }
    }
}