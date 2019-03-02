using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test
{
    class Program
    {
        static int Main(string[] args)
        {
            Type[] types = typeof(Program).Assembly.GetTypes();
            foreach (Type type in types)
            {
                if (!type.GetCustomAttributes(typeof(TestClassAttribute), false).Any())
                {
                    continue;
                }

                var instance = Activator.CreateInstance(type);
                foreach (System.Reflection.MethodInfo method in type.GetMethods())
                {
                    if (!method.GetCustomAttributes(typeof(TestMethodAttribute), false).Any())
                    {
                        continue;
                    }

                    GC.Collect(999, GCCollectionMode.Default, true);

                    Console.WriteLine($"Test : info {method.Name}: {type} {method}");
                    try
                    {
                        var action = (Action)method.CreateDelegate(typeof(Action), instance);
                        action();
                        Console.WriteLine("ok");
                    }
                    catch (Exception ex)
                    {
                        if (ex is AggregateException agex)
                        {
                            foreach (var inex in agex.InnerExceptions)
                            {
                                Debug.WriteLine(inex);
                                Console.WriteLine($"Test : error T0003: {inex}");
                            }
                        }
                        else
                        {
                            Debug.WriteLine(ex);
                            Console.WriteLine($"Test : error T0002: {ex}");
                        }

                        if (Debugger.IsAttached)
                        {
                            WaitExit();
                        }

                        return 1;
                    }
                    Console.WriteLine("---");
                }
            }
            if (Debugger.IsAttached)
            {
                WaitExit();
            }

            return 0;
        }

        static void WaitExit()
        {
            Console.Write("--- press enter to exit ---");
            while (Console.ReadKey(true).Key != ConsoleKey.Enter)
            {
                ;
            }
        }
    }
}
