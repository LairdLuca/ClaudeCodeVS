using System;
using System.Threading;

namespace DebuggerSample
{
    // A deliberately small program for tools/debug-check.js to stop inside. The loop gives a
    // conditional breakpoint something to be selective about, and the nested object gives
    // variable expansion something to expand.
    internal sealed class Customer
    {
        public string Name;
        public int Score;
    }

    internal sealed class Order
    {
        public int Id;
        public string Status;
        public Customer Customer;
    }

    internal static class Program
    {
        private static void Main()
        {
            for (int round = 1; round <= 8; round++)
            {
                var order = new Order
                {
                    Id = 1000 + round,
                    Status = round % 2 == 0 ? "Open" : "Closed",
                    Customer = new Customer { Name = "C" + round, Score = round * 7 }
                };

                Process(order, round);
                Thread.Sleep(400);
            }

            Console.WriteLine("done");
        }

        private static void Process(Order order, int round)
        {
            int total = order.Id + order.Customer.Score;
            Console.WriteLine(round + " -> " + total + " " + order.Status);
        }
    }
}
