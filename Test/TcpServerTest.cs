using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
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
            var id = "T" + MethodBase.GetCurrentMethod().GetHashCode().ToString("x4");
            int port = 2048 + id.Last();

            TcpServer server = new TcpServer();
            server.AcceptThreads = 10;
            server.AcceptBacklog = 100;
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
            var watch = Stopwatch.StartNew();
            int success = 0;

            var ip = IPAddress.Parse("127.0.0.1");
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
            var id = "T" + MethodBase.GetCurrentMethod().GetHashCode().ToString("x4");
            int port = 2048 + id.Last();

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
            var watch = Stopwatch.StartNew();
            var ip = IPAddress.Parse("127.0.0.1");

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
    }
}
