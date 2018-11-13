using Cave;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Test
{
    [TestClass]
    public class TcpServerTest
    {
        [TestMethod]
        public void TestAccept()
        {
            int port = Environment.TickCount % 1000 + 1000;

            TcpServer server = new TcpServer();
            server.AcceptThreads = 10;
            server.AcceptBacklog = 100;
            server.Listen(port);

            using (TcpAsyncClient client = new TcpAsyncClient())
            {
                client.Connect("::1", port);
                client.Send(new byte[1000]);
                client.Close();
            }
            using (TcpAsyncClient client = new TcpAsyncClient())
            {
                client.Connect("127.0.0.1", port);
                client.Send(new byte[1000]);
                client.Close();
            }

            int count = 100000;
            var watch = Stopwatch.StartNew();
            int success = 0;

            var ip = IPAddress.Parse("127.0.0.1");
            Parallel.For(1, count, new ParallelOptions() { MaxDegreeOfParallelism = 10 }, (n) =>
            {
                using (TcpAsyncClient client = new TcpAsyncClient())
                {
                    client.Connect(ip, port);
                    Interlocked.Increment(ref success);
                }
            });
            watch.Stop();

            Console.WriteLine($"{success} connections in {watch.Elapsed}");
            double cps = Math.Round(success / watch.Elapsed.TotalSeconds, 2);
            Console.WriteLine($"{cps} connections/s");
        }

        [TestMethod]
        public void TestSend()
        {
            int port = Environment.TickCount % 1000 + 2000;

            TcpServer server = new TcpServer();
            server.Listen(port);
            server.ClientAccepted += (s1, e1) =>
            {
                e1.Client.Received += (s2, e2) =>
                {
                    e2.Handled = true;
                };
            };

            long bytes = 0;
            var watch = Stopwatch.StartNew();
            var ip = IPAddress.Parse("127.0.0.1");
            Parallel.For(1, 16, (n) =>
            {
                using (TcpAsyncClient client = new TcpAsyncClient())
                {
                    client.Connect(ip, port);
                    for (int d = 0; d < 256; d++)
                    {
                        Interlocked.Add(ref bytes, 1024 * 1024);
                        client.Send(new byte[1024 * 1024]);
                    }
                    client.Close();
                }
                Trace.TraceInformation($"Client {n} completed.");
            });
            watch.Stop();

            Console.WriteLine($"{bytes.ToString("N")} bytes in {watch.Elapsed}");
            double bps = Math.Round(bytes / watch.Elapsed.TotalSeconds, 2);
            Console.WriteLine($"{bps.ToString("N")} bytes/s");
        }
    }
}

