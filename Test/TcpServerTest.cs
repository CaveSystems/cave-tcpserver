using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cave.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test
{
    [TestClass]
    public class TcpServerTest
    {
        [TestMethod]
        public void TestAccept()
        {
            string id = nameof(TestAccept);
            int port = 2048 + id.GetHashCode() % 1024;

            TcpServer server = new TcpServer
            {
                AcceptThreads = 10,
                AcceptBacklog = 100
            };
            server.Listen(port);
            Console.WriteLine($"Test : info {id}: Opened Server at port {port}.");

            using (TcpAsyncClient client = new TcpAsyncClient())
            {
                client.Connect("::1", port);
                client.Send(new byte[1000]);
                client.Close();
            }
            Console.WriteLine($"Test : info {id}: Test connect to ::1 successful.");

            using (TcpAsyncClient client = new TcpAsyncClient())
            {
                client.Connect("127.0.0.1", port);
                client.Send(new byte[1000]);
                client.Close();
            }
            Console.WriteLine($"Test : info {id}: Test connect to 127.0.0.1 successful.");

            int count = 10000;
            Stopwatch watch = Stopwatch.StartNew();
            int success = 0;

            IPAddress ip = IPAddress.Parse("127.0.0.1");
            Parallel.For(0, count, new ParallelOptions() { MaxDegreeOfParallelism = 10 }, (n) =>
            {
                using (TcpAsyncClient client = new TcpAsyncClient())
                {
                    client.Connect(ip, port);
                    Interlocked.Increment(ref success);
                }
            });
            watch.Stop();

            Console.WriteLine($"Test : info {id}: {success} connections in {watch.Elapsed}");
            double cps = Math.Round(success / watch.Elapsed.TotalSeconds, 2);
            Console.WriteLine($"Test : info {id}: {cps} connections/s");
        }

        [TestMethod]
        public void TestSend()
        {
            string id = nameof(TestSend);
            int port = 2048 + id.GetHashCode() % 1024;

            TcpServer server = new TcpServer();
            server.Listen(port);
            server.ClientAccepted += (s1, e1) =>
            {
                e1.Client.Received += (s2, e2) =>
                {
                    e2.Handled = true;
                };
            };
            Console.WriteLine($"Test : info {id}: Opened Server at port {port}.");

            long bytes = 0;
            Stopwatch watch = Stopwatch.StartNew();
            IPAddress ip = IPAddress.Parse("127.0.0.1");

            Parallel.For(0, 16, (n) =>
            {
                using (TcpAsyncClient client = new TcpAsyncClient())
                {
                    client.Connect(ip, port);
                    for (int d = 0; d < 256; d++)
                    {
                        client.Send(new byte[1024 * 1024]);
                        Interlocked.Add(ref bytes, 1024 * 1024);
                    }
                    client.Close();
                }
                Console.WriteLine($"Test : info {id}: Client {n + 1} completed.");
            });
            watch.Stop();

            Console.WriteLine($"Test : info {id}: {bytes.ToString("N")} bytes in {watch.Elapsed}");
            double bps = Math.Round(bytes / watch.Elapsed.TotalSeconds, 2);
            Console.WriteLine($"Test : info {id}: {bps.ToString("N")} bytes/s");
        }

        [TestMethod]
        public void TestDisconnectAsync()
        {
            int serverClientConnectedEventCount = 0;
            int serverClientDisconnectedEventCount = 0;
            int clientConnectedEventCount = 0;
            int clientDisconnectedEventCount = 0;

            string id = nameof(TestDisconnectAsync);
            int port = 2048 + id.GetHashCode() % 1024;

            TcpServer server = new TcpServer();
            server.Listen(port);
            server.ClientAccepted += (s1, e1) =>
            {
                e1.Client.Connected += (s2, e2) =>
                {
                    Interlocked.Increment(ref serverClientConnectedEventCount);
                };
                e1.Client.Disconnected += (s2, e2) =>
                {
                    Interlocked.Increment(ref serverClientDisconnectedEventCount);
                };
            };
            Console.WriteLine($"Test : info {id}: Opened Server at port {port}.");

            ConcurrentBag<TcpAsyncClient> clients = new ConcurrentBag<TcpAsyncClient>();
            IPAddress ip = IPAddress.Parse("127.0.0.1");
            Parallel.For(0, 1000, new ParallelOptions() { MaxDegreeOfParallelism = 10 }, (n) =>
            {
                TcpAsyncClient client = new TcpAsyncClient();
                client.Connected += (s1, e1) =>
                {
                    Interlocked.Increment(ref clientConnectedEventCount);
                };
                client.Disconnected += (s1, e1) =>
                {
                    Interlocked.Increment(ref clientDisconnectedEventCount);
                };
                client.Connect(ip, port);
                clients.Add(client);
            });
            //all clients connected
            Assert.AreEqual(1000, clientConnectedEventCount);
            //no client disconnected
            Assert.AreEqual(0, clientDisconnectedEventCount);

            Console.WriteLine($"Test : info {id}: ConnectedEventCount ok.");

            //give the server some more time
            Task.Delay(2000).Wait();

            //all clients connected
            Assert.AreEqual(clientConnectedEventCount, serverClientConnectedEventCount);
            Assert.AreEqual(1000, server.Clients.Length);
            //no client disconnected
            Assert.AreEqual(0, clientDisconnectedEventCount);
            Assert.AreEqual(clientDisconnectedEventCount, serverClientDisconnectedEventCount);

            Console.WriteLine($"Test : info {id}: DisconnectedEventCount ({clientDisconnectedEventCount}) ok.");

            //disconnect some
            int i = 0, disconnected = 0;
            foreach (TcpAsyncClient client in clients)
            {
                if (i++ % 3 == 0)
                {
                    disconnected++;
                    client.Close();
                }
            }

            Assert.AreEqual(disconnected, clientDisconnectedEventCount);

            //give the server some more time
            Task.Delay(2000).Wait();

            Assert.AreEqual(clientDisconnectedEventCount, serverClientDisconnectedEventCount);
            Assert.AreEqual(clientConnectedEventCount - disconnected, server.Clients.Length);

            Console.WriteLine($"Test : info {id}: DisconnectedEventCount ({clientDisconnectedEventCount}) ok.");

            foreach (TcpAsyncClient client in clients)
            {
                client.Close();
            }
            Assert.AreEqual(clientConnectedEventCount, serverClientConnectedEventCount);

            //give the server some more time
            Task.Delay(2000).Wait();

            Assert.AreEqual(clientDisconnectedEventCount, serverClientDisconnectedEventCount);

            Console.WriteLine($"Test : info {id}: DisconnectedEventCount ({clientDisconnectedEventCount}) ok.");
        }

        [TestMethod]
        public void TestPortAlreadyInUse1()
        {
            var listen = TcpListener.Create(2001);
            listen.Start();
            try
            {
                var server = new TcpServer();
                server.Listen(2001);
                Assert.Fail("AddressAlreadyInUse expected!");
            }
            catch(SocketException ex)
            {
                Assert.AreEqual(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
            }
            finally
            {
                listen.Stop();
            }
        }

        [TestMethod]
        public void TestPortAlreadyInUse2()
        {
            var listen = new TcpListener(IPAddress.Any, 2001);
            listen.Start();
            try
            {
                var server = new TcpServer();
                server.Listen(2001);
                Assert.Fail("AddressAlreadyInUse expected!");
            }
            catch (SocketException ex)
            {
                Assert.AreEqual(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
            }
            finally
            {
                listen.Stop();
            }
        }
    }
}
