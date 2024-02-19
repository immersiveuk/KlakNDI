﻿using Klak.Ndi.Interop;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using IntPtr = System.IntPtr;
using Marshal = System.Runtime.InteropServices.Marshal;
using CircularBuffer;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;

namespace Klak.Ndi 
{
    public sealed partial class NdiReceiver : MonoBehaviour
    {
        #region Receiver objects

        Interop.Recv _recv;
        FormatConverter _converter;
        MaterialPropertyBlock _override;

        void PrepareReceiverObjects()
        {
            if (_recv == null) _recv = RecvHelper.TryCreateRecv(NdiName);
            if (_converter == null) _converter = new FormatConverter(_resources);
            if (_override == null) _override = new MaterialPropertyBlock();
        }

        void ReleaseReceiverObjects()
        {
            _recv?.Dispose();
            _recv = null;

            _converter?.Dispose();
            _converter = null;

            // We don't dispose _override because it's reusable.
        }

        #endregion

        #region Receiver implementation
        public bool maintainAspectRatio = true;

        private Vector2 textureSize = Vector2.one;

        public void OnResized()
        {
            if (targetRenderer is not SpriteRenderer spriteRenderer) return;

            if (maintainAspectRatio)
            {
                float textureAspectRatio = textureSize.x / textureSize.y;
                float transformAspectRatio = spriteRenderer.transform.localScale.x / spriteRenderer.transform.localScale.y;

                if (textureAspectRatio > transformAspectRatio)
                    spriteRenderer.size = new Vector2(1, 1 / textureAspectRatio * transformAspectRatio);
                else
                    spriteRenderer.size = new Vector2(textureAspectRatio / transformAspectRatio, 1);
            }
            else
            {
                spriteRenderer.size = Vector2.one;
            }
        }

        private void DisplayTexture(RenderTexture rt)
        {
            textureSize = new Vector2(rt.width, rt.height);

            // Material property override
            if (targetRenderer != null)
            {
                targetRenderer.GetPropertyBlock(_override);
                _override.SetTexture(targetMaterialProperty, rt);
                targetRenderer.SetPropertyBlock(_override);

                OnResized();
            }

            // External texture update
            if (targetTexture != null)
                Graphics.Blit(rt, targetTexture);
        }

        void ReceiveVideoTask()
        {
            // Video frame capturing
            VideoFrame? videoFrame = RecvHelper.TryCaptureVideoFrame(_recv);

            if (videoFrame == null) return;// && audioFrame == null) return;
            VideoFrame frame = (VideoFrame)videoFrame;

            // Pixel format conversion
            RenderTexture rt = _converter.Decode(frame.Width, frame.Height, Util.HasAlpha(frame.FourCC), frame.Data);

            // Metadata retrieval
            if (frame.Metadata != IntPtr.Zero)
                metadata = Marshal.PtrToStringAnsi(frame.Metadata);
            else
                metadata = null;

            // Video frame release
            _recv.FreeVideoFrame(frame);
            DisplayTexture(rt);

        }

        void PrepareAudioSource(AudioFrame audioFrame)
        {
            if (_audioSource.isPlaying) return;

            // if the audio format changed, we need to create a new audio clip. 
            if (audioClip == null || audioClip.channels != audioFrame.NoChannels || audioClip.frequency != audioFrame.SampleRate)
                audioClip = AudioClip.Create("NdiReceiver_Audio", audioFrame.SampleRate, audioFrame.NoChannels, audioFrame.SampleRate, true); //Create a AudioClip that matches the incomming frame

            _audioSource.loop = true;
            _audioSource.clip = audioClip;
            _audioSource.Play();
        }

        void ReceiveAudioTask()
        {
            AudioFrame? audioFrame = RecvHelper.TryCaptureAudioFrame(_recv);
            if (audioFrame == null) return;
            AudioFrame frame = (AudioFrame)audioFrame;

            FillAudioBuffer(frame);

            _recv.FreeAudioFrame(frame);

            if (_audioSource == null || !_audioSource.enabled || !frame.HasData) return;
            PrepareAudioSource(frame);
        }

        #endregion

        #region Audio inplementation

        private AudioClip audioClip;
        private readonly object audioBufferLock = new();
        private const int BUFFER_SIZE = 1024 * 32;
        private readonly CircularBuffer<float> audioBuffer = new(BUFFER_SIZE);
        private bool m_bWaitForBufferFill = true;
        private const int m_iMinBufferAheadFrames = 8;
        private NativeArray<byte> m_aTempAudioPullBuffer;
        private AudioFrameInterleaved interleavedAudio = new();
        private float[] m_aTempSamplesArray = new float[1024 * 32];

        void OnAudioFilterRead(float[] data, int channels)
        {
            int length = data.Length;

            // STE: Waiting for enough read ahead buffer frames?
            if (m_bWaitForBufferFill)
            {
                // Are we good yet?
                // Should we be protecting audioBuffer.Size here?
                m_bWaitForBufferFill = (audioBuffer.Size < (length * m_iMinBufferAheadFrames));

                // Early out if not enough in the buffer still
                if (m_bWaitForBufferFill)
                {
                    return;
                }
            }

            // STE: Lock buffer for the smallest amount of time
            lock (audioBufferLock)
            {
                int iAudioBufferSize = audioBuffer.Size;

                // If we do not have enough data for a single frame then we will want to buffer up some read-ahead audio data. This will cause a longer gap in the audio playback, but this is better than more intermittent glitches I think
                m_bWaitForBufferFill = (iAudioBufferSize < length);
                if (!m_bWaitForBufferFill)
                {
                    audioBuffer.Front(ref data, data.Length);
                    audioBuffer.PopFront(data.Length);
                }
            }
        }

        void FillAudioBuffer(AudioFrame audio)
        {
            // Converted from NDI C# Managed sample code
            // we're working in bytes, so take the size of a 32 bit sample (float) into account
            int sizeInBytes = audio.NoSamples * audio.NoChannels * sizeof(float);

            // Unity is expecting interleaved audio and NDI uses planar.
            // create an interleaved frame and convert from the one we received
            interleavedAudio.SampleRate = audio.SampleRate;
            interleavedAudio.NoChannels = audio.NoChannels;
            interleavedAudio.NoSamples = audio.NoSamples;
            interleavedAudio.Timecode = audio.Timecode;


            // allocate native array to copy interleaved data into
            unsafe
            {
                if (m_aTempAudioPullBuffer == null || m_aTempAudioPullBuffer.Length < sizeInBytes)
                    m_aTempAudioPullBuffer = new NativeArray<byte>(sizeInBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                interleavedAudio.Data = (IntPtr)m_aTempAudioPullBuffer.GetUnsafePtr();
                if (interleavedAudio.Data != null)
                {
                    // Convert from float planar to float interleaved audio
                    _recv.AudioFrameToInterleaved(ref audio, ref interleavedAudio);

                    var totalSamples = interleavedAudio.NoSamples * interleavedAudio.NoChannels;
                    void* audioDataPtr = interleavedAudio.Data.ToPointer();

                    if (audioDataPtr != null)
                    {
                        // Grab data from native array
                        if (m_aTempSamplesArray == null || m_aTempSamplesArray.Length < totalSamples)
                        {
                            m_aTempSamplesArray = new float[totalSamples];
                        }
                        if (m_aTempSamplesArray != null)
                        {
                            for (int i = 0; i < totalSamples; i++)
                            {
                                m_aTempSamplesArray[i] = UnsafeUtility.ReadArrayElement<float>(audioDataPtr, i);
                            }
                        }

                        // Copy new sample data into the circular array
                        lock (audioBufferLock)
                        {
                            audioBuffer.PushBack(m_aTempSamplesArray, totalSamples);
                        }
                    }
                }
            }

        }
        #endregion

        #region Component state controller

        internal void Restart() => ReleaseReceiverObjects();

        #endregion

        #region MonoBehaviour implementation

        void OnDisable()
        {
            ReleaseReceiverObjects();
            if(m_aTempAudioPullBuffer.IsCreated)
                m_aTempAudioPullBuffer.Dispose();
        }

        void FixedUpdate()
        {
            PrepareReceiverObjects();
            if (_recv == null) return;

            ReceiveVideoTask();

            // Don't play audio in Editor mode.
            if (!Application.isPlaying) return;
            ReceiveAudioTask();

            // In a built project, the audio stutters. In Editor play testing, the audio works fine without this.
            // Not sure if Update is fast enough when built, but grabbing the audio a second time in FixedUpdate seems to fix the issue regardless.
            // I hope there's a slightly better way to do this, but this works for now.
            StartCoroutine(GetSoundAgain());
            IEnumerator GetSoundAgain()
            {
                yield return null;
                ReceiveAudioTask();
            }
        }

        #endregion
    
        public bool IsMuted => _audioSource != null && !_audioSource.enabled;

        public void SetAudioMuted(bool isMuted)
        {
            if (_audioSource != null)
                _audioSource.enabled = !isMuted;
        }

        public void SetSourceName(string sourceName)
            => NdiName = sourceName;
    }

} // namespace Klak.Ndi
