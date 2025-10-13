using UnityEngine;

[RequireComponent(typeof(PlaneSurfaceDrawer))]
public sealed class PlaneSurfaceDrawerMemoryAdapter : MonoBehaviour, IMemoryBudgetConsumer
{
    [SerializeField] private PlaneSurfaceDrawer drawer;

    private void Awake()
    {
        if (!drawer) drawer = GetComponent<PlaneSurfaceDrawer>();
    }

    public void ReleaseMemory()
    {
        if (drawer == null) return;
        drawer.ClearStrokes();
    }
}
