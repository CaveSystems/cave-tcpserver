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

                object instance = Activator.CreateInstance(type);
                foreach (System.Reflection.MethodInfo method in type.GetMethods())
                {
                    if (!method.GetCustomAttributes(typeof(TestMethodAttribute), false).Any())
                    {
                        continue;
                    }

                    string id = "T" + method.GetHashCode().ToString("x4");
                    Console.WriteLine($"Test : info {id}: {method}");
                    try
                    {
                        method.Invoke(instance, new object[0]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Test : error T0002: {ex}");
                        if (Debugger.IsAttached)
                        {
                            WaitExit();
                        }

                        return 1;
                    }
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
