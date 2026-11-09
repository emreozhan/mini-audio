// Tests for the levels software volume gives a session. They use a stand-in for the session's channel
// volumes, so no real audio session is touched.

using System;

namespace MiniAudio.Tests
{
    class FakeChannels : IChannelAudioVolume
    {
        public float[] Levels;
        public FakeChannels(params float[] levels) { Levels = levels; }
        public int GetChannelCount(out int count) { count = Levels.Length; return 0; }
        public int SetChannelVolume(int index, float level, ref Guid context) { Levels[index] = level; return 0; }
        public int GetChannelVolume(int index, out float level) { level = Levels[index]; return 0; }
    }

    static class SoftwareVolumeTests
    {
        public static void AddTo(TestList t)
        {
            t.Add("Software volume: channels changed behind MiniAudio's back are set to the gain again", delegate
            {
                // what a second writer left behind on a real machine: left at the gain, right at gain squared
                var fake = new FakeChannels(0.042853f, 0.001836f);
                Service(new Config()).ApplyLevels(Handle(fake, 0.042853f), "steam.exe");
                Check.Seq(new[] { 0.042853f, 0.042853f }, fake.Levels);
            });
            t.Add("Software volume: the app's saved balance is kept", delegate
            {
                var config = new Config();
                config.Balances["game.exe"] = -0.25f;
                var fake = new FakeChannels(1f, 1f);
                Service(config).ApplyLevels(Handle(fake, 0.5f), "game.exe");
                Check.Seq(new[] { 0.5f, 0.375f }, fake.Levels);
            });
            t.Add("Software volume: another app's balance is not used", delegate
            {
                var config = new Config();
                config.Balances["game.exe"] = -0.25f;
                var fake = new FakeChannels(1f, 1f);
                Service(config).ApplyLevels(Handle(fake, 0.5f), "browser.exe");
                Check.Seq(new[] { 0.5f, 0.5f }, fake.Levels);
            });
            t.Add("Software volume: a session without a known app gets the gain too", delegate
            {
                var fake = new FakeChannels(1f, 1f);
                Service(new Config()).ApplyLevels(Handle(fake, 0.2f), null);
                Check.Seq(new[] { 0.2f, 0.2f }, fake.Levels);
            });
            t.Add("Software volume: turning it off (gain 1) gives a changed session its full level back", delegate
            {
                var fake = new FakeChannels(0.042853f, 0.001836f);
                Service(new Config()).ApplyLevels(Handle(fake, 1f), "steam.exe");
                Check.Seq(new[] { 1f, 1f }, fake.Levels);
            });
            t.Add("Software volume: a balance being dragged (not saved yet) is the one applied", delegate
            {
                var service = Service(new Config());
                var fake = new FakeChannels(1f, 1f);
                var app = new AppSession { Key = "game.exe" };
                app.Handles.Add(Handle(fake, 0.5f));
                service.SetBalance(app, -0.5f, false);
                service.ApplyLevels(app.Handles[0], "game.exe");
                Check.Seq(new[] { 0.5f, 0.25f }, fake.Levels);
            });
        }

        // Never saves: the tests must not write the user's settings file
        static AppAudioService Service(Config config) { return new AppAudioService(new AudioService(null), config, true); }

        static SessionHandle Handle(FakeChannels channels, float gain) { return new SessionHandle { Channels = channels, Gain = gain }; }
    }
}
