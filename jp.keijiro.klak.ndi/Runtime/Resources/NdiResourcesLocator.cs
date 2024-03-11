namespace Klak.Ndi
{
    public static class NdiResourcesLocator
    {
        private static NdiResources _resources = null;
        public static NdiResources Resources => _resources ??= UnityEngine.Resources.Load<NdiResources>("NdiResources");
    }
}