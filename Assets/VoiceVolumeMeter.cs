using UnityEngine;

/// <summary>
/// Creates the 7-block “VU meter” around the *anchor* (former ProximityButton).
///  • bottom bar: 6  × 0.6  units  (level 0)
///  • 3 blocks up each side: 0.6 × 0.6 units (levels 1-3)
/// Public `radius` lets you pull the blocks in/out from the anchor.
/// </summary>
public class VoiceVolumeMeter : MonoBehaviour
{
    [Header("Block settings")]
    public float radius = 0.35f;          // distance from anchor to side-columns
    public Color idleColor = Color.gray;
    public Color activeColor = Color.red;

    const float BAR_W = 6f;  const float BAR_H = .6f;     // bottom bar
    const float COL = .6f;   const float ROW = .6f;       // side columns
    const int   LEVELS = 4;                               // 0–3 inclusive

    Transform[] blocks;                                   // 0 = bottom, 1-3 = side levels

    void Awake ()
    {
        
        blocks = new Transform[LEVELS];
        CreateBottomBar();          // level 0
        CreateSideColumn(+1);       // right  levels 1-3
        CreateSideColumn(-1);       // left   levels 1-3
        SetLevel(0);                // all dark
    }

    /* ---------- public API ---------- */
    /// <param name="fraction">0-1   (ex: .75 lights first 3 blocks)</param>
    public void SetVolumeFraction(float fraction)
    {
        int level = Mathf.Clamp(Mathf.CeilToInt(fraction * LEVELS) , 0, LEVELS);
        SetLevel(level);
    }

    /* ---------- internal ---------- */
    void SetLevel(int lvl)
    {
        for (int i = 0; i < blocks.Length; i++)
            blocks[i].GetComponent<Renderer>().material.color = (i < lvl ? activeColor : idleColor);
    }

    void CreateBottomBar()
    {
        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.transform.SetParent(transform, false);
        g.transform.localScale = new Vector3(BAR_W, BAR_H, BAR_H);
        g.transform.localPosition = new Vector3(0, -BAR_H * .5f, 0);
        blocks[0] = g.transform;
    }

    void CreateSideColumn(int dir)
    {
        for (int i = 1; i <= 3; i++)
        {
            GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.transform.SetParent(transform, false);
            c.transform.localScale    = new Vector3(COL, ROW, ROW);
            c.transform.localPosition = new Vector3(dir * (BAR_W * .5f + COL * .5f + radius),
                                                    ROW * (.5f + (i - 1)), 0);
            blocks[i] = c.transform;
        }
    }
}
