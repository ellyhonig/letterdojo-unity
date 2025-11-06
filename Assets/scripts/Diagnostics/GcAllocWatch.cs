using Unity.Profiling;
using UnityEngine;

namespace Diagnostics
{
    /// <summary>
    /// Logs frames that allocate managed heap memory, highlighting spikes without the Profiler UI.
    /// </summary>
    public sealed class GcAllocWatch : MonoBehaviour
    {
        ProfilerRecorder _gcAllocated;

        void OnEnable()
        {
            _gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        }

        void OnDisable()
        {
            _gcAllocated.Dispose();
        }

        void LateUpdate()
        {
            if (_gcAllocated.Valid && _gcAllocated.LastValue > 0)
            {
                float kb = _gcAllocated.LastValue / 1024f;
                Debug.Log($"[GcAllocWatch] Frame allocated {kb:0.0} KB at t={Time.time:0.00}s");
            }
        }
    }
}
