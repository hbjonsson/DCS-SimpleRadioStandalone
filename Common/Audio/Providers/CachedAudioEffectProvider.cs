using System;
using System.Collections.Generic;
using System.IO;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Providers;

public class CachedAudioEffectProvider
{
    public delegate Stream CachedEffectsLoaderDelegate(string fileName);

    private static CachedAudioEffectProvider _instance;

    private readonly string sourceFolder;

    private CachedAudioEffectProvider()
    {
        sourceFolder = AppDomain.CurrentDomain.BaseDirectory + "\\AudioEffects\\";
        LoadEffects();
    }

    //Used on Android or other platforms to change the loader system
    public static CachedEffectsLoaderDelegate CachedEffectsLoader { get; set; }

    private Dictionary<string, CachedAudioEffect> _ambientAudioEffects { get; } = new();

    public List<CachedAudioEffect> RadioTransmissionStart { get; set; }
    public List<CachedAudioEffect> RadioTransmissionEnd { get; set; }

    public List<CachedAudioEffect> IntercomTransmissionStart { get; set; }
    public List<CachedAudioEffect> IntercomTransmissionEnd { get; set; }

    public static CachedAudioEffectProvider Instance
    {
        get
        {
            if (_instance == null) _instance = new CachedAudioEffectProvider();
            //stops cyclic init
            return _instance;
        }
    }

    public CachedAudioEffect SelectedRadioTransmissionStartEffect
    {
        get
        {
            var selectedTone = GlobalSettingsStore.Instance.ProfileSettingsStore
                .GetClientSettingString(ProfileSettingsKeys.RadioTransmissionStartSelection).ToLowerInvariant();

            foreach (var startEffect in RadioTransmissionStart)
                if (startEffect.FileName.ToLowerInvariant().Equals(selectedTone))
                    return startEffect;

            return RadioTransmissionStart[0];
        }
    }

    public CachedAudioEffect SelectedRadioTransmissionEndEffect
    {
        get
        {
            var selectedTone = GlobalSettingsStore.Instance.ProfileSettingsStore
                .GetClientSettingString(ProfileSettingsKeys.RadioTransmissionEndSelection).ToLowerInvariant();

            foreach (var endEffect in RadioTransmissionEnd)
                if (endEffect.FileName.ToLowerInvariant().Equals(selectedTone))
                    return endEffect;

            return RadioTransmissionEnd[0];
        }
    }

    public CachedAudioEffect SelectedIntercomTransmissionStartEffect
    {
        get
        {
            var selectedTone = GlobalSettingsStore.Instance.ProfileSettingsStore
                .GetClientSettingString(ProfileSettingsKeys.IntercomTransmissionStartSelection).ToLowerInvariant();

            foreach (var startEffect in IntercomTransmissionStart)
                if (startEffect.FileName.ToLowerInvariant().Equals(selectedTone))
                    return startEffect;

            return IntercomTransmissionStart[0];
        }
    }

    public CachedAudioEffect SelectedIntercomTransmissionEndEffect
    {
        get
        {
            var selectedTone = GlobalSettingsStore.Instance.ProfileSettingsStore
                .GetClientSettingString(ProfileSettingsKeys.IntercomTransmissionEndSelection).ToLowerInvariant();

            foreach (var endEffect in IntercomTransmissionEnd)
                if (endEffect.FileName.ToLowerInvariant().Equals(selectedTone))
                    return endEffect;

            return IntercomTransmissionEnd[0];
        }
    }

    public CachedAudioEffect KY58EncryptionTransmitTone { get; set; }
    public CachedAudioEffect KY58EncryptionEndTone { get; set; }
    public CachedAudioEffect NATOTone { get; set; }
    public CachedAudioEffect MIDSTransmitTone { get; set; }
    public CachedAudioEffect MIDSEndTone { get; set; }

    public CachedAudioEffect HAVEQUICKTone { get; set; }

    public void LoadEffects()
    {
        // Build into locals and swap whole lists: audio threads read these while settings reload them.
        var custom = GlobalSettingsStore.Instance.GetClientSetting(GlobalSettingsKeys.AudioEffectsFolder).RawValue;
        var files = EffectFiles(custom?.Trim(), sourceFolder);
        RadioTransmissionStart = LoadTransmissionEffects(files, CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START);
        RadioTransmissionEnd = LoadTransmissionEffects(files, CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END);
        IntercomTransmissionStart =
            LoadTransmissionEffects(files, CachedAudioEffect.AudioEffectTypes.INTERCOM_TRANS_START);
        IntercomTransmissionEnd = LoadTransmissionEffects(files, CachedAudioEffect.AudioEffectTypes.INTERCOM_TRANS_END);

        KY58EncryptionTransmitTone =
            new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.KY_58_TX, CachedEffectsLoader);
        KY58EncryptionEndTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.KY_58_RX, CachedEffectsLoader);

        NATOTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.NATO_TONE, CachedEffectsLoader);

        MIDSTransmitTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.MIDS_TX, CachedEffectsLoader);
        MIDSEndTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.MIDS_TX_END, CachedEffectsLoader);

        HAVEQUICKTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.HAVEQUICK_TONE, CachedEffectsLoader);
    }

    /// <summary>
    /// Effect files from each folder in order; an earlier folder's file wins over a same-named later one,
    /// so the user's custom folder (kept across updates) overrides bundled sounds.
    /// </summary>
    internal static List<string> EffectFiles(params string[] folders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;
            foreach (var path in Directory.EnumerateFiles(folder))
                if (seen.Add(Path.GetFileName(path)))
                    files.Add(path);
        }

        return files;
    }

    private List<CachedAudioEffect> LoadTransmissionEffects(List<string> files,
        CachedAudioEffect.AudioEffectTypes type)
    {
        var prefix = type.ToString().ToLowerInvariant();
        var effects = new List<CachedAudioEffect>();
        foreach (var effectPath in files)
        {
            var effect = Path.GetFileName(effectPath);
            if (!effect.ToLowerInvariant().StartsWith(prefix))
                continue;

            var audioEffect = new CachedAudioEffect(type, effect, effectPath, CachedEffectsLoader);
            if (audioEffect.AudioEffectFloat != null) effects.Add(audioEffect);
        }

        //IF the audio folder is missing - to avoid a crash, init with a blank one
        if (effects.Count == 0)
            effects.Add(new CachedAudioEffect(type, CachedEffectsLoader));

        return effects;
    }

    //TODO unload ever?
    public CachedAudioEffect GetAmbientEffect(string name)
    {
        name = name.ToLowerInvariant();
        if (_ambientAudioEffects.TryGetValue(name, out var effect)) return effect;

        effect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.AMBIENT_COCKPIT,
            name + ".wav",
            AppDomain.CurrentDomain.BaseDirectory +
            $"{Path.DirectorySeparatorChar}AudioEffects{Path.DirectorySeparatorChar}Ambient{Path.DirectorySeparatorChar}" +
            name + ".wav", CachedEffectsLoader);

        _ambientAudioEffects[name] = effect;

        return effect;
    }
}