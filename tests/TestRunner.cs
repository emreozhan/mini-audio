// Runs the tests of every area. Build and run: see README (Tests).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace MiniAudio.Tests
{
    class TestList : List<KeyValuePair<string, Action>>
    {
        public void Add(string name, Action body) { Add(new KeyValuePair<string, Action>(name, body)); }
    }

    static class TestRunner
    {
        [STAThread]
        static int Main()
        {
            var tests = new TestList();
            ChannelMathTests.AddTo(tests);
            SoftwareVolumeTests.AddTo(tests);
            TrayLookTests.AddTo(tests);
            FlyoutFooterTests.AddTo(tests);

            int passed = 0, failed = 0;
            foreach (var t in tests)
            {
                try { t.Value(); passed++; Console.WriteLine("PASS  " + t.Key); }
                catch (Exception e)
                {
                    if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                    failed++;
                    Console.WriteLine("FAIL  " + t.Key + "\n      " + e.Message);
                }
            }
            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }

    static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("expected " + Show(expected) + ", got " + Show(actual));
        }

        public static void Near(float expected, float actual)
        {
            if (Math.Abs(expected - actual) > 1e-4f)
                throw new Exception("expected " + F(expected) + ", got " + F(actual));
        }

        public static void Seq(float[] expected, float[] actual)
        {
            bool same = expected == null ? actual == null : actual != null && expected.Length == actual.Length;
            for (int i = 0; same && expected != null && i < expected.Length; i++) same = Math.Abs(expected[i] - actual[i]) <= 1e-4f;
            if (!same) throw new Exception("expected " + J(expected) + ", got " + J(actual));
        }

        static string Show(object v) { return v == null ? "null" : v is string ? "\"" + v + "\"" : v.ToString(); }
        static string F(float v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }

        static string J(float[] a)
        {
            if (a == null) return "null";
            var s = new string[a.Length];
            for (int i = 0; i < a.Length; i++) s[i] = F(a[i]);
            return "[" + string.Join(", ", s) + "]";
        }
    }
}
