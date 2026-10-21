// Tests for the pure channel math behind balance and software volume.
// Build and run: see README (Tests).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MiniAudio.Tests
{
    static class TestRunner
    {
        static int passed, failed;

        static int Main()
        {
            var tests = new List<KeyValuePair<string, Action>>
            {
                T("GainFromDb: 0 dB is full gain", delegate { Near(1f, ChannelMath.GainFromDb(0f)); }),
                T("GainFromDb: -20 dB is one tenth", delegate { Near(0.1f, ChannelMath.GainFromDb(-20f)); }),
                T("GainFromDb: -6 dB is about half", delegate { Near(0.5f, ChannelMath.GainFromDb(-6.0206f)); }),
                T("GainFromDb: positive dB is clamped to 1", delegate { Near(1f, ChannelMath.GainFromDb(3f)); }),
                T("GainFromDb: unreadable (NaN) leaves the level alone", delegate { Near(1f, ChannelMath.GainFromDb(float.NaN)); }),

                T("Levels: center at full gain is all ones", delegate { Seq(new[] { 1f, 1f }, ChannelMath.Levels(0f, 1f, 2)); }),
                T("Levels: left balance lowers the right channel", delegate { Seq(new[] { 1f, 0.75f }, ChannelMath.Levels(-0.25f, 1f, 2)); }),
                T("Levels: right balance with gain scales both", delegate { Seq(new[] { 0.1f, 0.2f }, ChannelMath.Levels(0.5f, 0.2f, 2)); }),
                T("Levels: mono gets only the gain", delegate { Seq(new[] { 0.3f }, ChannelMath.Levels(0.4f, 0.3f, 1)); }),
                T("Levels: surround channels get the gain", delegate { Seq(new[] { 0.5f, 0f, 0.5f, 0.5f, 0.5f, 0.5f }, ChannelMath.Levels(-1f, 0.5f, 6)); }),
                T("Levels: balance beyond -1 is clamped", delegate { Seq(new[] { 1f, 0f }, ChannelMath.Levels(-3f, 1f, 2)); }),
                T("Levels: no channels gives an empty array", delegate { Seq(new float[0], ChannelMath.Levels(0f, 1f, 0)); }),

                T("Balance: louder left is negative", delegate { Near(-0.25f, ChannelMath.Balance(1f, 0.75f)); }),
                T("Balance: louder right is positive", delegate { Near(0.25f, ChannelMath.Balance(0.75f, 1f)); }),
                T("Balance: scaled-down channels keep their balance", delegate { Near(-0.25f, ChannelMath.Balance(0.001f, 0.00075f)); }),
                T("Balance: equal channels are centered", delegate { Near(0f, ChannelMath.Balance(0.5f, 0.5f)); }),
                T("Balance: silent channels are centered", delegate { Near(0f, ChannelMath.Balance(0f, 0f)); }),

                T("Normalize: scaled stereo gets its loudest channel back to 1", delegate { Seq(new[] { 1f, 0.75f }, ChannelMath.Normalize(new[] { 0.08f, 0.06f })); }),
                T("Normalize: unscaled levels stay the same", delegate { Seq(new[] { 1f, 0.75f }, ChannelMath.Normalize(new[] { 1f, 0.75f })); }),
                T("Normalize: all-zero levels become ones", delegate { Seq(new[] { 1f, 1f }, ChannelMath.Normalize(new[] { 0f, 0f })); }),
            };

            foreach (var t in tests)
            {
                try { t.Value(); passed++; Console.WriteLine("PASS  " + t.Key); }
                catch (Exception e) { failed++; Console.WriteLine("FAIL  " + t.Key + "\n      " + e.Message); }
            }
            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static KeyValuePair<string, Action> T(string name, Action body) { return new KeyValuePair<string, Action>(name, body); }

        static void Near(float expected, float actual)
        {
            if (Math.Abs(expected - actual) > 1e-4f)
                throw new Exception("expected " + F(expected) + ", got " + F(actual));
        }

        static void Seq(float[] expected, float[] actual)
        {
            if (actual == null) throw new Exception("expected [" + J(expected) + "], got null");
            bool same = expected.Length == actual.Length;
            for (int i = 0; same && i < expected.Length; i++) same = Math.Abs(expected[i] - actual[i]) <= 1e-4f;
            if (!same) throw new Exception("expected [" + J(expected) + "], got [" + J(actual) + "]");
        }

        static string F(float v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }
        static string J(float[] a) { var s = new string[a.Length]; for (int i = 0; i < a.Length; i++) s[i] = F(a[i]); return string.Join(", ", s); }
    }
}
