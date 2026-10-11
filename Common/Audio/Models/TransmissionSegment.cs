using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Models
{
    // https://stackoverflow.com/questions/538060/proper-use-of-the-idisposable-interface#538238
    public class TransmissionSegment
    {
        // Non-owning - belongs to a pool!
        public float[] Audio { get; }
        public bool HasEncryption { get; }
        public bool Decryptable { get; }
        public string OriginalClientGuid { get; }
        public string ClientGuid { get; }
        public bool IsSecondary {  get; }
        public Modulation Modulation { get; }
        public double ReceivingPower { get; }
        public float LineOfSightLoss { get; }
        public bool NoAudioEffects { get; }

        /// <summary>Transmit frequency in Hz (local DSP context; not a network field).</summary>
        public double Frequency { get; }

        /// <summary>
        /// Linear radio volume from overlay slider / cockpit knob
        /// (<see cref="DeJitteredTransmission.Volume"/>). Applied after OpenFreq AGC.
        /// </summary>
        public float Volume { get; }

        public TransmissionSegment(DeJitteredTransmission transmission)
        {
            Audio = new float[transmission.PCMAudioLength];

            transmission.PCMMonoAudio.AsSpan(0, transmission.PCMAudioLength).CopyTo(Audio);
            
            HasEncryption = transmission.Encryption > 0;
            Decryptable = transmission.Decryptable;
            OriginalClientGuid = transmission.OriginalClientGuid;
            IsSecondary = transmission.IsSecondary;
            Modulation = transmission.Modulation;
            ClientGuid = transmission.Guid;
            ReceivingPower = transmission.ReceivingPower;
            LineOfSightLoss = transmission.LineOfSightLoss;
            NoAudioEffects = transmission.NoAudioEffects;
            Frequency = transmission.Frequency;
            Volume = transmission.Volume;
        }

        /// <summary>
        /// Local-only constructor for preview / tests that already hold processed PCM.
        /// </summary>
        public TransmissionSegment(
            float[] audio,
            Modulation modulation,
            double frequencyHz,
            double receivingPower = 0.05,
            float lineOfSightLoss = 0f,
            string clientGuid = "PREVIEW",
            bool decryptable = true)
        {
            Audio = audio ?? Array.Empty<float>();
            Modulation = modulation;
            Frequency = frequencyHz;
            ReceivingPower = receivingPower;
            LineOfSightLoss = lineOfSightLoss;
            ClientGuid = clientGuid;
            OriginalClientGuid = clientGuid;
            Decryptable = decryptable;
            HasEncryption = !decryptable;
            IsSecondary = false;
            NoAudioEffects = false;
            Volume = 1f;
        }
    }
}
