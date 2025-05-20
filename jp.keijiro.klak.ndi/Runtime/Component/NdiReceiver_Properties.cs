using System;
using UnityEngine;

namespace Klak.Ndi 
{
    public sealed partial class NdiReceiver : MonoBehaviour
    {
        #region NDI source settings

        [SerializeField] string _ndiName = null;
        string _ndiNameRuntime;
        public event Action OnSourceChanged;

        public event Action<bool> OnMutedChanged;
        internal void FireOnMutedChanged(bool muted) => OnMutedChanged?.Invoke(muted);

        public string NdiName
        {
          get => _ndiNameRuntime;
          set => SetNdiName(value);
        }

        void SetNdiName(string name)
        {
            if (_ndiNameRuntime == name) return;
            _ndiName = _ndiNameRuntime = name;
            OnSourceChanged?.Invoke();
            Restart();
            OnResized();
        }

        #endregion

        #region Target settings

        [SerializeField] RenderTexture _targetTexture = null;

        public RenderTexture targetTexture
          { get => _targetTexture;
            set => _targetTexture = value; }

        [SerializeField] Renderer _targetRenderer = null;

        public Renderer targetRenderer
          { get => _targetRenderer;
            set => _targetRenderer = value; }

        [SerializeField] string _targetMaterialProperty = null;

        public string targetMaterialProperty
          { get => _targetMaterialProperty;
            set => _targetMaterialProperty = value; }

        public AudioSource _audioSource;
        #endregion

        #region Runtime property

        public RenderTexture texture => _converter?.LastDecoderOutput;

        public string metadata { get; set; }

        public Interop.Recv internalRecvObject => _recv;

        #endregion

        #region Resources asset reference

        [SerializeField, HideInInspector] NdiResources _resources = null;

        public void SetResources(NdiResources resources)
          => _resources = resources;

        #endregion

        #region Editor change validation

        // Applies changes on the serialized fields to the runtime properties.
        // We use OnValidate on Editor, which also works as an initializer.
        // Player never call it, so we use Awake instead of it.

        #if UNITY_EDITOR
        void OnValidate()
        #else
        void Awake()
        #endif
          => NdiName = _ndiName;

        #endregion
    }

} // namespace Klak.Ndi
