using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MassiveParallelizationTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Tester tester = new Tester();
            if (tester.Init())
            {
                List<string> userIds = tester.GetUserList().Result;
                //tester.LoadUsersInParallel(new List<string> { "user5560" });
                //tester.LoadUsersInParallel(userIds);
                tester.LoadUsersSequentialAsync(userIds).Wait();
                Console.ReadLine();
            }
            else
            {
                Console.WriteLine("Initialization failed");
                Console.ReadLine();
            }
        }


    }
}
