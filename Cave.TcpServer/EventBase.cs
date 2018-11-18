#region CopyRight 2018
/*
    Copyright (c) 2007-2018 Andreas Rohleder (andreas@rohleder.cc)
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
#endregion Authors & Contributors

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Cave.Net
{
    /// <summary>
    /// Provides an OnError event and a generic CallEvent function.
    /// </summary>
    public abstract class EventBase
    {
        /// <summary>Calls the specified event with the specified parameters.</summary>
        /// <typeparam name="TEventArgs"></typeparam>
        /// <param name="callback">The delegate to call.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="callAsync">Create a threadpool thread to run the callback</param>
        protected void CallEvent<TEventArgs>(EventHandler<TEventArgs> callback, TEventArgs args, bool callAsync = false) where TEventArgs : EventArgs
        {
            if (callback == null)
            {
                return;
            }

            if (callAsync)
            {
                Task.Factory.StartNew((p) =>
                {
                    var kv = (KeyValuePair<EventHandler<TEventArgs>, TEventArgs>)p;
                    CallEvent(kv.Key, kv.Value);
                }, new KeyValuePair<EventHandler<TEventArgs>, TEventArgs>(callback, args));
                return;
            }
            try
            {
                callback.Invoke(this, args);
            }
            catch (Exception ex)
            {
                if (!callback.Equals(Error))
                {
                    OnError(new ExceptionEventArgs(ex));
                }
            }
        }

        /// <summary>
        /// Calls the <see cref="Error"/> event (if set).
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnError(ExceptionEventArgs e)
        {
            CallEvent(Error, e);
        }

        /// <summary>
        /// Event to be called after an exception occured
        /// </summary>
        public event EventHandler<ExceptionEventArgs> Error;
    }
}
