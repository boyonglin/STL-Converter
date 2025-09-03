using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityVolumeRendering;

[ExecuteAlways]
public class StlErrorPainter : MonoBehaviour
{
    [Header("Scene Roots")]
    public Transform dicomRoot;          // 含 VolumeRenderedObject
    public Transform stlRoot;            // STL 父物件（多個子件）

    [Header("Units")]
    [Tooltip("1 Unity 單位有多少 mm：若 1U=1m → 1000；1U=1mm → 1")]
    public float mmPerUnityUnit = 1000f;

    [Header("Probe (per-vertex)")]
    [Tooltip("從頂點沿法向外推這麼多再發射 Ray（避免剛好在表面）")]
    public float surfaceEpsMm = 0.2f;
    [Tooltip("射線最大量測距離（mm）")]
    public float maxProbeMm = 10f;
    [Tooltip("若第一次沒打中，會改用反向再試一次")]
    public bool tryBothDirections = true;
    [Tooltip("Signed（沿 STL 法向）或 Absolute（絕對值）")]
    public bool signedMode = true;

    [Header("Color mapping")] [Tooltip("顏色下限/上限（mm），大於上限會夾住顏色")]
    public float colorMinMm = 0f;
    public float colorMaxMm = 1.0f;
    [Tooltip("是否使用離散色帶（如圖示等級）")]
    public bool useBands = true;
    public float[] bandEdgesMm = new float[] {0.1f,0.2f,0.3f,0.4f,0.5f,0.6f,0.7f,0.8f,0.9f,1.0f};
    public Color[] bandColors = new Color[] {
        new Color(0.10f,0.65f,1f), // 0-0.1
        new Color(0.25f,0.80f,0.9f),
        new Color(0.40f,0.90f,0.60f),
        new Color(0.85f,0.95f,0.20f),
        new Color(0.98f,0.80f,0.20f),
        new Color(0.98f,0.55f,0.20f),
        new Color(0.95f,0.30f,0.20f),
        new Color(0.85f,0.15f,0.15f),
        new Color(0.70f,0.10f,0.10f),
        new Color(0.55f,0.05f,0.05f), // > last edge
    };

    [Header("Stats")]
    [Tooltip("統計時忽略 |error| > cutoffMm 的樣本（保留顯示）")]
    public float cutoffMm = 3f;
    public bool exportCsv = false;

    [Header("UI")]
    public bool showEditorProgressBar = true;
    public bool cancelable = true;
    public int progressEvery = 4000;

    // internals
    VolumeRaycaster _caster = new VolumeRaycaster();
    bool _running; long _done, _total;

    [ContextMenu("Bake Colors + Histogram")]
    public void Bake()
    {
        RunCoroutineSmart(BakeCoroutine());
    }

    IEnumerator BakeCoroutine()
    {
        if (!dicomRoot || !stlRoot) { Debug.LogError("請指定 dicomRoot 與 stlRoot"); yield break; }
        var vols = dicomRoot.GetComponentsInChildren<VolumeRenderedObject>(false);
        if (vols.Length == 0) { Debug.LogError("dicomRoot 底下找不到 VolumeRenderedObject"); yield break; }

        var mfs = stlRoot.GetComponentsInChildren<MeshFilter>(false);
        if (mfs.Length == 0) { Debug.LogError("stlRoot 底下找不到 MeshFilter"); yield break; }

        float eps = surfaceEpsMm / mmPerUnityUnit;
        float maxDist = maxProbeMm / mmPerUnityUnit;

        // 準備材質（簡單頂點色 Shader）
        Shader vtxShader = Shader.Find("Unlit/VertexColor");
        if (!vtxShader) Debug.LogWarning("找不到 Unlit/VertexColor，請先建立下面提供的 shader。");

        // 收集工作量
        long nVerts = 0;
        foreach (var mf in mfs) if (mf && mf.sharedMesh) nVerts += mf.sharedMesh.vertexCount;
        _total = nVerts; _done = 0; _running = true;

        // 統計池
        var samplesAll = new List<float>((int)Mathf.Min(nVerts, int.MaxValue));
        int miss = 0, nan = 0;

        int tick = 0;
        foreach (var mf in mfs)
        {
            if (!mf || !mf.sharedMesh) continue;

            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;
            var norms = mesh.normals;
            if (norms == null || norms.Length != verts.Length)
            {
                mesh.RecalculateNormals();
                norms = mesh.normals;
            }

            var colors = new Color[verts.Length];

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 pW = mf.transform.TransformPoint(verts[i]);
                Vector3 nW = mf.transform.TransformDirection(norms[i]).normalized;
                float valMm;
                if (TryProbeSigned(pW, nW, maxDist, eps, out float signedMm))
                {
                    valMm = signedMode ? Mathf.Abs(signedMm) : Mathf.Abs(signedMm); // 給顏色用 magnitude；你要雙色可改
                    samplesAll.Add(Mathf.Abs(signedMm));
                    colors[i] = MapColor(valMm);
                }
                else
                {
                    miss++;
                    colors[i] = Color.gray; // miss 顯示灰
                }

                _done++;
                if (++tick >= Mathf.Max(64, progressEvery))
                {
                    if (UpdateProgress($"Baking colors {mf.name}… {_done}/{_total}")) { _running = false; yield break; }
                    tick = 0; yield return null;
                }
            }

            // 套材質＋頂點色
            mesh.colors = colors;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr && vtxShader && (!mr.sharedMaterial || mr.sharedMaterial.shader != vtxShader))
            {
                var mat = new Material(vtxShader);
                mat.name = "VertexColor (error)";
                mr.sharedMaterial = mat;
            }
        }

        // ---- 統計與直方圖 ----
        var kept = (cutoffMm > 0) ? samplesAll.Where(x => x <= cutoffMm).ToList() : new List<float>(samplesAll);
        kept.Sort();
        float mean = kept.Count > 0 ? kept.Average() : 0f;
        float p50 = Percentile(kept, 50), p95 = Percentile(kept, 95), p99 = Percentile(kept, 99);
        float coverage03 = (samplesAll.Count > 0) ? samplesAll.Count(x => x <= 0.3f) / (float)samplesAll.Count : 0f;

        // 直方圖（與圖示類似：每 0.1 mm 一格到 1.0 mm）
        float[] edges = new float[] {0.1f,0.2f,0.3f,0.4f,0.5f,0.6f,0.7f,0.8f,0.9f,1.0f, 2.0f, 3.0f};
        int[] hist = new int[edges.Length+1];
        foreach (var v in samplesAll)
        {
            int bin = 0;
            while (bin < edges.Length && v > edges[bin]) bin++;
            hist[bin]++;
        }

        // 印結果
        Debug.Log($"[StlErrorPainter] verts={_total}, used={samplesAll.Count}, miss={miss}, nan={nan}");
        Debug.Log($"[StlErrorPainter] (cutoff<={cutoffMm:F1}mm)  Mean={mean:F3} mm, P50={p50:F3}, P95={p95:F3}, P99={p99:F3}");
        Debug.Log($"[StlErrorPainter] Coverage@0.3mm = {coverage03*100f:F1}%");
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("Range(mm)\tCount");
        float lo = 0f;
        for (int i = 0; i < hist.Length; i++)
        {
            float hi = (i < edges.Length) ? edges[i] : float.PositiveInfinity;
            sb.AppendLine($"{lo:F1}-{(float.IsInfinity(hi)?999:hi):F1}\t{hist[i]}");
            lo = hi;
        }
        Debug.Log(sb.ToString());

        if (exportCsv)
        {
            string path = Path.Combine(Application.dataPath, "stl_error_histogram.csv");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[StlErrorPainter] CSV: {path}");
        }

        ClearProgress();
        _running = false;
        yield break;
    }

    // —— 以 STL 法向做雙向射線量測，回 signed(mm) —— 
    bool TryProbeSigned(Vector3 pW, Vector3 nW, float maxDistU, float epsU, out float signedMm)
    {
        signedMm = 0f;
        // 先從外往內
        if (SafeRaycast(pW + nW * epsU, -nW, maxDistU, out Vector3 q))
        {
            float s = Vector3.Dot(q - pW, nW);
            signedMm = s * mmPerUnityUnit;
            return true;
        }
        // 再從內往外
        if (tryBothDirections && SafeRaycast(pW - nW * epsU, nW, maxDistU, out q))
        {
            float s = Vector3.Dot(q - pW, nW);
            signedMm = s * mmPerUnityUnit;
            return true;
        }
        return false;
    }

    bool SafeRaycast(Vector3 origin, Vector3 dir, float maxDistU, out Vector3 hitPoint)
    {
        try
        {
            var ray = new Ray(origin, dir);
            if (_caster.RaycastScene(ray, out var hit))
            {
                hitPoint = hit.point;
                // 限距（避免不合理穿越）
                if ((hitPoint - origin).magnitude <= maxDistU + 1e-6f) return true;
            }
        }
        catch { }
        hitPoint = default;
        return false;
    }

    Color MapColor(float valMm)
    {
        float v = Mathf.Clamp(valMm, colorMinMm, colorMaxMm);
        if (useBands && bandEdgesMm != null && bandEdgesMm.Length > 0)
        {
            int i = 0;
            while (i < bandEdgesMm.Length && v > bandEdgesMm[i]) i++;
            i = Mathf.Clamp(i, 0, bandColors.Length - 1);
            return bandColors[i];
        }
        // 連續漸層（藍→青→綠→黃→紅）
        float t = Mathf.InverseLerp(colorMinMm, colorMaxMm, v);
        return Jet(t);
    }

    // 簡單 Jet colormap
    static Color Jet(float t)
    {
        t = Mathf.Clamp01(t);
        float r = Mathf.Clamp01(1.5f - Mathf.Abs(4f*t - 3f));
        float g = Mathf.Clamp01(1.5f - Mathf.Abs(4f*t - 2f));
        float b = Mathf.Clamp01(1.5f - Mathf.Abs(4f*t - 1f));
        return new Color(r,g,b,1f);
    }

    static float Percentile(List<float> sorted, float p)
    {
        if (sorted == null || sorted.Count == 0) return 0f;
        float rank = (p / 100f) * (sorted.Count - 1);
        int lo = Mathf.FloorToInt(rank), hi = Mathf.CeilToInt(rank);
        if (lo == hi) return sorted[lo];
        return Mathf.Lerp(sorted[lo], sorted[hi], rank - lo);
    }

    bool UpdateProgress(string label)
    {
#if UNITY_EDITOR
        if (showEditorProgressBar)
        {
            float pr = (_total>0)? Mathf.Clamp01((float)_done/_total) : 0f;
            if (cancelable)
            {
                if (EditorUtility.DisplayCancelableProgressBar("STL Error Painter", label, pr))
                { EditorUtility.ClearProgressBar(); return true; }
            }
            else EditorUtility.DisplayProgressBar("STL Error Painter", label, pr);
        }
#endif
        return false;
    }
    void ClearProgress(){
#if UNITY_EDITOR
        if (showEditorProgressBar) EditorUtility.ClearProgressBar();
#endif 
    }

    // 允許 Editor/Play 都能跑
    void RunCoroutineSmart(IEnumerator co)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) { StartEditorCoroutine(co); return; }
#endif
        StartCoroutine(co);
    }

#if UNITY_EDITOR
    IEnumerator _editorCo;
    void StartEditorCoroutine(IEnumerator co)
    {
        _editorCo = co;
        EditorApplication.update += EditorUpdate;
    }
    void EditorUpdate()
    {
        if (_editorCo == null) { EditorApplication.update -= EditorUpdate; return; }
        try { if (!_editorCo.MoveNext()) { _editorCo = null; EditorApplication.update -= EditorUpdate; } }
        catch { _editorCo = null; EditorApplication.update -= EditorUpdate; }
    }
#endif
}
