using System;
using System.Runtime.InteropServices;

namespace Cave.Net
{
    /// <summary>
    /// Provides a fast TcpServer implementation using the default TcpServerClient class.
    /// For own client implementations use <see cref="TcpServer{TcpServerClient}"/>
    /// </summary>
    /// <seealso cref="IDisposable" />
    [ComVisible(false)]
    public class TypedTcpServer<TClient> : TcpServer<TClient> where TClient : TcpAsyncClient<TypedTcpServer<TClient>>, new()
    {
    }
}
