// Tests for the pure channel math behind balance and software volume.

namespace MiniAudio.Tests
{
    static class ChannelMathTests
    {
        public static void AddTo(TestList t)
        {
            t.Add("GainFromDb: 0 dB is full gain", delegate { Check.Near(1f, ChannelMath.GainFromDb(0f)); });
            t.Add("GainFromDb: -20 dB is one tenth", delegate { Check.Near(0.1f, ChannelMath.GainFromDb(-20f)); });
            t.Add("GainFromDb: -6 dB is about half", delegate { Check.Near(0.5f, ChannelMath.GainFromDb(-6.0206f)); });
            t.Add("GainFromDb: positive dB is clamped to 1", delegate { Check.Near(1f, ChannelMath.GainFromDb(3f)); });
            t.Add("GainFromDb: unreadable (NaN) leaves the level alone", delegate { Check.Near(1f, ChannelMath.GainFromDb(float.NaN)); });

            t.Add("Levels: center at full gain is all ones", delegate { Check.Seq(new[] { 1f, 1f }, ChannelMath.Levels(0f, 1f, 2)); });
            t.Add("Levels: left balance lowers the right channel", delegate { Check.Seq(new[] { 1f, 0.75f }, ChannelMath.Levels(-0.25f, 1f, 2)); });
            t.Add("Levels: right balance with gain scales both", delegate { Check.Seq(new[] { 0.1f, 0.2f }, ChannelMath.Levels(0.5f, 0.2f, 2)); });
            t.Add("Levels: mono gets only the gain", delegate { Check.Seq(new[] { 0.3f }, ChannelMath.Levels(0.4f, 0.3f, 1)); });
            t.Add("Levels: surround channels get the gain", delegate { Check.Seq(new[] { 0.5f, 0f, 0.5f, 0.5f, 0.5f, 0.5f }, ChannelMath.Levels(-1f, 0.5f, 6)); });
            t.Add("Levels: balance beyond -1 is clamped", delegate { Check.Seq(new[] { 1f, 0f }, ChannelMath.Levels(-3f, 1f, 2)); });
            t.Add("Levels: no channels gives an empty array", delegate { Check.Seq(new float[0], ChannelMath.Levels(0f, 1f, 0)); });

            t.Add("Balance: louder left is negative", delegate { Check.Near(-0.25f, ChannelMath.Balance(1f, 0.75f)); });
            t.Add("Balance: louder right is positive", delegate { Check.Near(0.25f, ChannelMath.Balance(0.75f, 1f)); });
            t.Add("Balance: scaled-down channels keep their balance", delegate { Check.Near(-0.25f, ChannelMath.Balance(0.001f, 0.00075f)); });
            t.Add("Balance: equal channels are centered", delegate { Check.Near(0f, ChannelMath.Balance(0.5f, 0.5f)); });
            t.Add("Balance: silent channels are centered", delegate { Check.Near(0f, ChannelMath.Balance(0f, 0f)); });

            t.Add("ForNewSession: a full-level session is left alone",
                delegate { Check.Seq(null, ChannelMath.ForNewSession(new[] { 1f, 1f }, null, false, 1f)); });
            t.Add("ForNewSession: a session left lowered gets its full level back",
                delegate { Check.Seq(new[] { 1f, 1f }, ChannelMath.ForNewSession(new[] { 0.08f, 0.08f }, null, false, 1f)); });
            t.Add("ForNewSession: a lowered session with uneven channels and no saved balance is centered",
                delegate { Check.Seq(new[] { 1f, 1f }, ChannelMath.ForNewSession(new[] { 0.042853f, 0.001836f }, null, false, 1f)); });
            t.Add("ForNewSession: uneven channels at full level are left alone",
                delegate { Check.Seq(null, ChannelMath.ForNewSession(new[] { 1f, 0.75f }, null, false, 1f)); });
            t.Add("ForNewSession: the saved balance is applied",
                delegate { Check.Seq(new[] { 1f, 0.75f }, ChannelMath.ForNewSession(new[] { 1f, 1f }, -0.25f, false, 1f)); });
            t.Add("ForNewSession: the saved balance carries the software volume gain",
                delegate { Check.Seq(new[] { 0.1f, 0.2f }, ChannelMath.ForNewSession(new[] { 1f, 1f }, 0.5f, true, 0.2f)); });
            t.Add("ForNewSession: on a software volume device a lowered session is left to the timer",
                delegate { Check.Seq(null, ChannelMath.ForNewSession(new[] { 0.3f, 0.3f }, null, true, 0.3f)); });
            t.Add("ForNewSession: a session without channels is left alone",
                delegate { Check.Seq(null, ChannelMath.ForNewSession(new float[0], -0.25f, false, 1f)); });
        }
    }
}
