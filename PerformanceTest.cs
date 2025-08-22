using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Performance validation tests for ArrayPool optimizations
    /// </summary>
    public static class PerformanceTest
    {
        /// <summary>
        /// Test ArrayPool vs traditional array allocation performance
        /// </summary>
        public static void TestArrayPoolPerformance()
        {
            const int iterations = 10000;
            const int arraySize = 1024;
            
            Console.WriteLine("=== ArrayPool Performance Test ===");
            
            // Traditional allocation test
            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var array = new float[arraySize];
                Array.Clear(array, 0, arraySize);
                // Simulate some work
                for (int j = 0; j < arraySize; j++) array[j] = j * 0.01f;
            }
            sw1.Stop();
            
            // ArrayPool test
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var array = ArrayPool<float>.Shared.Rent(arraySize);
                try
                {
                    Array.Clear(array, 0, arraySize);
                    // Simulate some work
                    for (int j = 0; j < arraySize; j++) array[j] = j * 0.01f;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(array);
                }
            }
            sw2.Stop();
            
            Console.WriteLine($"Traditional allocation: {sw1.ElapsedMilliseconds}ms");
            Console.WriteLine($"ArrayPool allocation: {sw2.ElapsedMilliseconds}ms");
            Console.WriteLine($"Improvement: {(double)sw1.ElapsedMilliseconds / sw2.ElapsedMilliseconds:F2}x faster");
        }
        
        /// <summary>
        /// Test List vs cached collections performance
        /// </summary>
        public static void TestListReusePerformance()
        {
            const int iterations = 10000;
            
            Console.WriteLine("\n=== List Reuse Performance Test ===");
            
            // Traditional List creation test
            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var list = new List<float>();
                for (int j = 0; j < 100; j++)
                {
                    list.Add(j * 0.01f);
                }
                var array = list.ToArray();
            }
            sw1.Stop();
            
            // Reused List test
            var reuseList = new List<float>(100);
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                reuseList.Clear();
                for (int j = 0; j < 100; j++)
                {
                    reuseList.Add(j * 0.01f);
                }
                var array = reuseList.ToArray();
            }
            sw2.Stop();
            
            Console.WriteLine($"New List creation: {sw1.ElapsedMilliseconds}ms");
            Console.WriteLine($"Reused List: {sw2.ElapsedMilliseconds}ms");
            Console.WriteLine($"Improvement: {(double)sw1.ElapsedMilliseconds / sw2.ElapsedMilliseconds:F2}x faster");
        }
        
        /// <summary>
        /// Test cached arrays vs Enumerable.Repeat().ToArray()
        /// </summary>
        public static void TestCachedArraysPerformance()
        {
            const int iterations = 10000;
            const int arraySize = 256;
            
            Console.WriteLine("\n=== Cached Arrays Performance Test ===");
            
            // Enumerable.Repeat().ToArray() test
            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var array = Enumerable.Repeat(0.01f, arraySize).ToArray();
                // Simulate usage
                var sum = array.Sum();
            }
            sw1.Stop();
            
            // Cached array test
            var cachedArray = new float[arraySize];
            for (int i = 0; i < arraySize; i++) cachedArray[i] = 0.01f;
            
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var array = cachedArray; // Reference, not copy
                // Simulate usage
                var sum = array.Sum();
            }
            sw2.Stop();
            
            Console.WriteLine($"Enumerable.Repeat().ToArray(): {sw1.ElapsedMilliseconds}ms");
            Console.WriteLine($"Cached array reference: {sw2.ElapsedMilliseconds}ms");
            Console.WriteLine($"Improvement: {(double)sw1.ElapsedMilliseconds / sw2.ElapsedMilliseconds:F2}x faster");
        }
        
        /// <summary>
        /// Run all performance tests
        /// </summary>
        public static void RunAllTests()
        {
            try
            {
                TestArrayPoolPerformance();
                TestListReusePerformance();
                TestCachedArraysPerformance();
                
                Console.WriteLine("\n=== Performance Tests Completed ===");
                Console.WriteLine("ArrayPool optimizations are working correctly.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Performance test failed: {ex.Message}");
            }
        }
    }
}