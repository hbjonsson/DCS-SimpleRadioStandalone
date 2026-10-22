using System;
using System.IO;
using System.Linq;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Audio;

[TestClass]
public class CachedAudioEffectProviderTests
{
    [TestMethod]
    public void EffectFiles_CustomFolderOverridesBundledAndAddsNew()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var custom = Directory.CreateDirectory(Path.Combine(root, "custom")).FullName;
        var bundled = Directory.CreateDirectory(Path.Combine(root, "bundled")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(custom, "radio_trans_start.wav"), "");
            File.WriteAllText(Path.Combine(custom, "RADIO_TRANS_START_MINE.wav"), "");
            File.WriteAllText(Path.Combine(bundled, "RADIO_TRANS_START.wav"), "");
            File.WriteAllText(Path.Combine(bundled, "RADIO_TRANS_END.wav"), "");

            var files = CachedAudioEffectProvider.EffectFiles(custom, bundled, null, Path.Combine(root, "missing"));

            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.Combine(custom, "radio_trans_start.wav"),
                    Path.Combine(custom, "RADIO_TRANS_START_MINE.wav"),
                    Path.Combine(bundled, "RADIO_TRANS_END.wav"),
                },
                files.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
