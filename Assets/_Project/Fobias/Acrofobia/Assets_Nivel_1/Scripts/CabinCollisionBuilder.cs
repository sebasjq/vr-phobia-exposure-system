#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Specific to the ForestWatchTower model in Acrofobia_1, reviewed in PR #76.
// Adds static boxes for three decks and three stair flights. Original colliders stay enabled.
public sealed class CabinCollisionBuilder : EditorWindow
{
    private const string ModelGuid = "58f1151d96bc9644c8cb7754c1b0a861";
    private const string OutputName = "Cabana_Colisiones_Reforzadas";
    private const float RawMinHeight = -5.5975671f;
    private const float RawMaxHeight = 7.6386423f;
    private float gridStep = 0.08f;
    private float floorThickness = 0.25f;
    private float rampThickness = 0.12f;

    private sealed class Triangle
    {
        public Vector3 a, b, c, center;
        public float area, up;
        public Triangle(Vector3 x, Vector3 y, Vector3 z)
        {
            a = x; b = y; c = z; center = (a + b + c) / 3f;
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float twiceArea = normal.magnitude;
            area = twiceArea * 0.5f;
            up = twiceArea > 1e-10f ? normal.y / twiceArea : 0f;
        }
        public void AddPoints(List<Vector3> output) { output.Add(a); output.Add(b); output.Add(c); }
    }
    private sealed class Tread
    {
        public Vector3 weightedCenter, center;
        public float area;
        public readonly List<Vector3> points = new List<Vector3>();
    }
    private sealed class Floor
    {
        public float top;
        public bool[] mask;
        public float[] samples;
        public readonly List<float> plateHeights = new List<float>();
        public readonly List<RectInt> rectangles = new List<RectInt>();
    }
    private sealed class WallBox
    {
        public Vector3 center, size;
    }
    private sealed class Ramp
    {
        public Vector3 start, end;
        public float width, angle;
    }
    private static readonly Vector2[] FloorBands =
    { new Vector2(-2.66f, -2.35f), new Vector2(-0.03f, 0.20f), new Vector2(2.50f, 2.83f) };
    private static readonly Vector2[] StairBands =
    { new Vector2(-4.50f, -2.76f), new Vector2(-2.20f, -0.12f), new Vector2(0.25f, 2.15f) };

    [MenuItem("Tools/Fobias/Cabana/Mejorar suelos y escaleras")]
    private static void Open()
    {
        var window = GetWindow<CabinCollisionBuilder>("Colisiones cabana");
        window.minSize = new Vector2(410f, 230f);
    }
    private void OnGUI()
    {
        EditorGUILayout.LabelField("WatchTower - Acrofobia 1", EditorStyles.boldLabel);
        gridStep = EditorGUILayout.Slider("Rejilla de suelos (m)", gridStep, 0.04f, 0.12f);
        floorThickness = EditorGUILayout.Slider("Grosor de suelos (m)", floorThickness, 0.15f, 0.35f);
        rampThickness = EditorGUILayout.Slider("Grosor de rampas (m)", rampThickness, 0.08f, 0.20f);
        EditorGUILayout.HelpBox("Abre Acrofobia_1 fuera de Play. La herramienta localiza WatchTower, " +
            "rehace suelos y rampas, refuerza las paredes y coloca el rig sobre el piso. " +
            "Conserva el tamaño de WatchTower y reemplaza el refuerzo anterior.", MessageType.Info);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Rehacer colisiones y ajustar inicio", GUILayout.Height(30))) Build();
    }

    private void Build()
    {
        GameObject output = null;
        int undoGroup = -1;
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode || scene.name != "Acrofobia_1" ||
                PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Abre la escena Acrofobia_1 fuera de Play y de Prefab Mode.");
            MeshFilter source = null;
            GameObject previousOutput = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == OutputName) previousOutput = root;
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null || filter.sharedMesh.name.IndexOf("LOD0", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    if (AssetDatabase.AssetPathToGUID(path) != ModelGuid) continue;
                    if (source != null) throw new InvalidOperationException("Se encontro mas de una cabaña. Debe haber una sola WatchTower en esta escena.");
                    source = filter;
                }
            }
            if (source == null) throw new InvalidOperationException("No se encontro ForestWatchTower_LOD0 del modelo WatchTower revisado.");
            List<Triangle> world = ReadTriangles(source, out Bounds worldBounds);
            float scale = worldBounds.size.y / (RawMaxHeight - RawMinHeight);
            if (scale <= 0.01f || scale > 10f) throw new InvalidOperationException("La escala del modelo queda fuera del intervalo esperado.");
            // Use the lower flight to align the collision grid with the building, regardless of its yaw.
            List<Tread> firstFlight = FindTreads(world, worldBounds, scale, StairBands[0]);
            Vector3 forward = firstFlight[firstFlight.Count - 1].center - firstFlight[0].center;
            forward.y = 0f; forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Vector3 origin = new Vector3(source.transform.position.x, 0f, source.transform.position.z);
            Vector3 ToFrame(Vector3 p)
            {
                Vector3 delta = p - origin;
                return new Vector3(Vector3.Dot(delta, right), p.y, Vector3.Dot(delta, forward));
            }
            var triangles = new List<Triangle>(world.Count);
            Bounds bounds = new Bounds(ToFrame(world[0].a), Vector3.zero);
            foreach (Triangle t in world)
            {
                var local = new Triangle(ToFrame(t.a), ToFrame(t.b), ToFrame(t.c));
                triangles.Add(local); bounds.Encapsulate(local.a); bounds.Encapsulate(local.b); bounds.Encapsulate(local.c);
            }
            float step = Mathf.Clamp(gridStep, 0.04f, 0.12f);
            Vector2 gridOrigin = new Vector2(bounds.min.x - 2f * step, bounds.min.z - 2f * step);
            int nx = Mathf.CeilToInt((bounds.max.x - gridOrigin.x) / step) + 3;
            int nz = Mathf.CeilToInt((bounds.max.z - gridOrigin.y) / step) + 3;
            if ((long)nx * nz > 250000) throw new InvalidOperationException("La rejilla es demasiado grande. Aumenta su separacion.");
            var floors = new List<Floor>();
            for (int i = 0; i < 3; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Colisiones cabana", "Analizando plataforma " + (i + 1), i / 6f))
                    throw new OperationCanceledException();
                floors.Add(BuildFloor(triangles, bounds, scale, FloorBands[i], gridOrigin, nx, nz, step));
            }
            var ramps = new List<Ramp>();
            for (int i = 0; i < 3; i++)
            {
                List<Tread> treads = FindTreads(triangles, bounds, scale, StairBands[i]);
                ramps.Add(BuildRamp(treads, i == 0 ? treads[0].center.y - 0.2f * scale : floors[i - 1].top,
                    floors[i], gridOrigin, nx, nz, step, scale));
            }
            List<WallBox> walls = BuildWalls(triangles, bounds, scale, floors[2].top, step);
            int boxCount = 3 + walls.Count;
            foreach (Floor floor in floors) boxCount += floor.rectangles.Count;
            if (boxCount > 500) throw new InvalidOperationException("Se generarian demasiados colliders. Aumenta la rejilla o revisa el modelo.");

            // All geometry and landing checks finish before the scene is changed.
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rehacer colisiones de cabana y ajustar inicio");
            if (previousOutput != null) Undo.DestroyObjectImmediate(previousOutput);
            output = new GameObject(OutputName);
            SceneManager.MoveGameObjectToScene(output, scene);
            Undo.RegisterCreatedObjectUndo(output, "Crear colisiones de cabana");
            output.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(forward, Vector3.up));
            output.transform.localScale = Vector3.one;
            output.layer = source.gameObject.layer;
            for (int i = 0; i < floors.Count; i++)
            {
                GameObject group = Child(output, "Suelo_" + (i + 1));
                int index = 0;
                foreach (RectInt rect in floors[i].rectangles)
                {
                    float plateHeight = floors[i].plateHeights[index];
                    GameObject box = Child(group, "Placa_" + (++index));
                    box.transform.localPosition = new Vector3(gridOrigin.x + (rect.x + rect.width * 0.5f) * step,
                        plateHeight - floorThickness * 0.5f, gridOrigin.y + (rect.y + rect.height * 0.5f) * step);
                    BoxCollider collider = box.AddComponent<BoxCollider>();
                    collider.size = new Vector3(rect.width * step + 0.006f, floorThickness, rect.height * step + 0.006f);
                }
            }
            for (int i = 0; i < ramps.Count; i++)
            {
                Ramp ramp = ramps[i];
                Vector3 tangent = (ramp.end - ramp.start).normalized;
                Vector3 horizontal = tangent; horizontal.y = 0f; horizontal.Normalize();
                Vector3 normal = (Vector3.up - horizontal * Mathf.Tan(ramp.angle * Mathf.Deg2Rad)).normalized;
                GameObject box = Child(output, "Rampa_" + (i + 1));
                box.transform.localPosition = (ramp.start + ramp.end) * 0.5f - normal * (rampThickness * 0.5f);
                box.transform.localRotation = Quaternion.LookRotation(tangent, normal);
                BoxCollider collider = box.AddComponent<BoxCollider>();
                collider.size = new Vector3(ramp.width, rampThickness, Vector3.Distance(ramp.start, ramp.end));
            }
            GameObject wallGroup = Child(output, "Paredes_Solidas");
            for (int i = 0; i < walls.Count; i++)
            {
                GameObject wall = Child(wallGroup, "Pared_" + (i + 1));
                wall.transform.localPosition = walls[i].center;
                wall.AddComponent<BoxCollider>().size = walls[i].size;
            }
            ConfigurePlayer(scene, output);
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = output;
            Debug.Log("Cabaña reforzada: " + boxCount + " BoxCollider, 3 plataformas, 3 rampas y " + walls.Count + " refuerzos de pared. Pendientes: " +
                ramps[0].angle.ToString("F1") + ", " + ramps[1].angle.ToString("F1") + ", " + ramps[2].angle.ToString("F1") +
                " grados. Guarda la escena y comprueba el ascenso y el descenso en Play y en el visor.", output);
            output = null;
        }
        catch (OperationCanceledException) { Debug.Log("Generacion cancelada; los colliders originales se conservan."); }
        catch (Exception e) { Debug.LogException(e); EditorUtility.DisplayDialog("Colisiones cabana", e.Message, "Cerrar"); }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (output != null)
            {
                if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
                else DestroyImmediate(output);
            }
        }
    }

    private static GameObject Child(GameObject parent, string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        child.layer = parent.layer;
        return child;
    }
    private static List<Triangle> ReadTriangles(MeshFilter source, out Bounds bounds)
    {
        var result = new List<Triangle>();
        bounds = new Bounds();
        Matrix4x4 matrix = source.transform.localToWorldMatrix;
        using (Mesh.MeshDataArray data = MeshUtility.AcquireReadOnlyMeshData(source.sharedMesh))
        {
            Mesh.MeshData md = data[0];
            using (var vertices = new NativeArray<Vector3>(md.vertexCount, Allocator.Temp))
            {
                md.GetVertices(vertices);
                var world = new Vector3[vertices.Length];
                for (int i = 0; i < world.Length; i++)
                {
                    world[i] = matrix.MultiplyPoint3x4(vertices[i]);
                    if (i == 0) bounds = new Bounds(world[i], Vector3.zero); else bounds.Encapsulate(world[i]);
                }
                for (int s = 0; s < md.subMeshCount; s++)
                {
                    SubMeshDescriptor descriptor = md.GetSubMesh(s);
                    if (descriptor.topology != MeshTopology.Triangles) continue;
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        md.GetIndices(indices, s, true);
                        for (int t = 0; t + 2 < indices.Length; t += 3)
                            result.Add(new Triangle(world[indices[t]], world[indices[t + 1]], world[indices[t + 2]]));
                    }
                }
            }
        }
        if (result.Count < 1000) throw new InvalidOperationException("La malla LOD0 no corresponde al modelo esperado.");
        return result;
    }
    private static float RawHeight(float worldHeight, Bounds bounds, float scale)
        => (worldHeight - bounds.min.y) / scale + RawMinHeight;

    private static List<Tread> FindTreads(List<Triangle> triangles, Bounds bounds, float scale, Vector2 band)
    {
        var groups = new Dictionary<int, Tread>();
        foreach (Triangle t in triangles)
        {
            float h = RawHeight(t.center.y, bounds, scale);
            if (t.up < 0.995f || h < band.x || h > band.y || t.area < 1e-8f) continue;
            int key = Mathf.RoundToInt(h / 0.02f);
            if (!groups.TryGetValue(key, out Tread group)) { group = new Tread(); groups.Add(key, group); }
            group.area += t.area; group.weightedCenter += t.center * t.area; t.AddPoints(group.points);
        }
        var result = new List<Tread>();
        foreach (Tread group in groups.Values)
        {
            if (group.area < 0.4f * scale * scale || group.area > 0.8f * scale * scale) continue;
            Bounds span = new Bounds(group.points[0], Vector3.zero);
            foreach (Vector3 point in group.points) span.Encapsulate(point);
            if (new Vector2(span.size.x, span.size.z).magnitude > 1.9f * scale) continue;
            group.center = group.weightedCenter / group.area; result.Add(group);
        }
        result.Sort((a, b) => a.center.y.CompareTo(b.center.y));
        if (result.Count < 6) throw new InvalidOperationException("No se identificaron suficientes peldaños en un tramo. Revisa la orientacion y la version de WatchTower.");
        return result;
    }
    private static Floor BuildFloor(List<Triangle> triangles, Bounds bounds, float scale, Vector2 band,
        Vector2 origin, int nx, int nz, float step)
    {
        var floor = new Floor { top = float.NegativeInfinity, mask = new bool[nx * nz], samples = new float[nx * nz] };
        for (int i = 0; i < floor.samples.Length; i++) floor.samples[i] = float.NaN;
        foreach (Triangle t in triangles)
        {
            if (t.up < 0.94f || RawHeight(Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y)), bounds, scale) < band.x - 0.007f ||
                RawHeight(Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y)), bounds, scale) > band.y + 0.007f) continue;
            floor.top = Mathf.Max(floor.top, Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y)) + 0.01f);
            Rasterize(t, floor.mask, origin, nx, nz, step, floor.samples);
        }
        if (float.IsNegativeInfinity(floor.top)) throw new InvalidOperationException("No se encontro una de las plataformas.");
        // A one-cell closing fills seams between boards while preserving large stair openings.
        var dilated = new bool[nx * nz];
        for (int z = 1; z < nz - 1; z++) for (int x = 1; x < nx - 1; x++)
            if (floor.mask[z * nx + x])
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) dilated[(z + dz) * nx + x + dx] = true;
        var occupied = (bool[])floor.mask.Clone();
        for (int z = 1; z < nz - 1; z++) for (int x = 1; x < nx - 1; x++)
        {
            bool enclosed = true;
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) enclosed &= dilated[(z + dz) * nx + x + dx];
            floor.mask[z * nx + x] |= enclosed;
            occupied[z * nx + x] = floor.mask[z * nx + x];
        }
        for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++)
        {
            if (!occupied[z * nx + x]) continue;
            int width = 1;
            while (x + width < nx && occupied[z * nx + x + width]) width++;
            int height = 1;
            while (z + height < nz)
            {
                bool full = true;
                for (int dx = 0; dx < width; dx++) full &= occupied[(z + height) * nx + x + dx];
                if (!full) break; height++;
            }
            for (int dz = 0; dz < height; dz++) for (int dx = 0; dx < width; dx++) occupied[(z + dz) * nx + x + dx] = false;
            floor.rectangles.Add(new RectInt(x, z, width, height));
            var heights = new List<float>();
            for (int dz = 0; dz < height; dz++) for (int dx = 0; dx < width; dx++)
            {
                float sample = floor.samples[(z + dz) * nx + x + dx];
                if (!float.IsNaN(sample)) heights.Add(sample);
            }
            heights.Sort();
            floor.plateHeights.Add(heights.Count > 0 ? heights[heights.Count / 2] + 0.01f : floor.top);
        }
        if (floor.rectangles.Count == 0) throw new InvalidOperationException("Una plataforma no tiene superficie util.");
        var representative = new List<float>(floor.plateHeights); representative.Sort();
        floor.top = representative[representative.Count / 2];
        return floor;
    }
    private static void Rasterize(Triangle t, bool[] mask, Vector2 origin, int nx, int nz, float step, float[] heights = null)
    {
        Vector2 a = new Vector2(t.a.x, t.a.z) - origin, b = new Vector2(t.b.x, t.b.z) - origin,
            c = new Vector2(t.c.x, t.c.z) - origin;
        float den = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
        if (Mathf.Abs(den) < 1e-10f) return;
        int x0 = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / step - 0.5f));
        int x1 = Mathf.Min(nx - 1, Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / step - 0.5f));
        int z0 = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / step - 0.5f));
        int z1 = Mathf.Min(nz - 1, Mathf.FloorToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / step - 0.5f));
        for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
        {
            float px = (x + 0.5f) * step, pz = (z + 0.5f) * step;
            float u = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (pz - c.y)) / den;
            float v = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (pz - c.y)) / den;
            if (u >= -1e-5f && v >= -1e-5f && 1f - u - v >= -1e-5f)
            {
                int cell = z * nx + x; mask[cell] = true;
                if (heights != null)
                {
                    float h = u * t.a.y + v * t.b.y + (1f - u - v) * t.c.y;
                    heights[cell] = float.IsNaN(heights[cell]) ? h : Mathf.Max(heights[cell], h);
                }
            }
        }
    }
    private static bool Supported(Floor floor, Vector3 point, Vector2 origin, int nx, int nz, float step)
    {
        int x = Mathf.FloorToInt((point.x - origin.x) / step), z = Mathf.FloorToInt((point.z - origin.y) / step);
        return x >= 0 && x < nx && z >= 0 && z < nz && floor.mask[z * nx + x];
    }
    private static Ramp BuildRamp(List<Tread> treads, float bottom, Floor upper,
        Vector2 origin, int nx, int nz, float step, float scale)
    {
        Vector3 direction = treads[treads.Count - 1].center - treads[0].center;
        direction.y = 0f; direction.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, direction);
        float meanS = 0f, meanY = 0f;
        foreach (Tread tread in treads) { meanS += Vector3.Dot(tread.center, direction); meanY += tread.center.y; }
        meanS /= treads.Count; meanY /= treads.Count;
        float numerator = 0f, denominator = 0f;
        foreach (Tread tread in treads)
        {
            float ds = Vector3.Dot(tread.center, direction) - meanS;
            numerator += ds * (tread.center.y - meanY); denominator += ds * ds;
        }
        if (denominator < 1e-6f) throw new InvalidOperationException("Un tramo no tiene longitud suficiente.");
        float slope = numerator / denominator, intercept = float.NegativeInfinity;
        float minSide = float.PositiveInfinity, maxSide = float.NegativeInfinity;
        foreach (Tread tread in treads) foreach (Vector3 p in tread.points)
        {
            intercept = Mathf.Max(intercept, p.y - slope * Vector3.Dot(p, direction) + 0.01f);
            minSide = Mathf.Min(minSide, Vector3.Dot(p, side)); maxSide = Mathf.Max(maxSide, Vector3.Dot(p, side));
        }
        if (slope <= 0.01f) throw new InvalidOperationException("La pendiente de un tramo no es valida.");
        float first = (bottom - intercept) / slope, last = (upper.top - intercept) / slope;
        float across = (minSide + maxSide) * 0.5f;
        Vector3 start = direction * first + side * across + Vector3.up * bottom;
        float landingDistance = -1f, originalLength = last - first;
        for (float distance = 0f; distance <= originalLength + 0.8f * scale; distance += step * 0.25f)
            if (Supported(upper, start + direction * distance, origin, nx, nz, step)) { landingDistance = distance; break; }
        if (landingDistance <= 0.5f * originalLength)
            throw new InvalidOperationException("No se encontro una conexion segura entre la escalera y su plataforma. No se ha modificado la escena.");
        // Reach the landing height at its leading edge; overlap the first cells slightly.
        float length = landingDistance + Mathf.Min(0.04f, step * 0.5f);
        Vector3 landingPoint = start + direction * length;
        float targetTop = TopAt(upper, landingPoint, origin, nx, step);
        foreach (Tread tread in treads) foreach (Vector3 point in tread.points)
            length = Mathf.Max(length, Vector3.Dot(point - start, direction) + 0.01f);
        float finalSlope = (targetTop - bottom) / length;
        foreach (Tread tread in treads) foreach (Vector3 point in tread.points)
        {
            float along = Vector3.Dot(point - start, direction);
            if (along > 0.001f) finalSlope = Mathf.Max(finalSlope, (point.y + 0.008f - bottom) / along);
        }
        Vector3 end = start + direction * length + Vector3.up * (finalSlope * length);
        if (end.y - TopAt(upper, end, origin, nx, step) > 0.15f)
            throw new InvalidOperationException("La rampa termina demasiado alta sobre el descanso.");
        if (!Supported(upper, end, origin, nx, nz, step))
            throw new InvalidOperationException("El extremo de una rampa no coincide con su descanso. No se ha modificado la escena.");
        float angle = Mathf.Atan(finalSlope) * Mathf.Rad2Deg;
        if (angle >= 48f) throw new InvalidOperationException("La rampa supera 48 grados. Revisa la escala y orientacion del modelo.");
        foreach (Tread tread in treads) foreach (Vector3 point in tread.points)
        {
            float t = Vector3.Dot(point - start, direction);
            if (t < -0.01f || t > length + 0.01f || bottom + finalSlope * t < point.y - 0.001f)
                throw new InvalidOperationException("La rampa no cubre todos los peldaños. No se ha modificado la escena.");
        }
        return new Ramp { start = start, end = end, width = maxSide - minSide + 0.03f, angle = angle };
    }
    private static float TopAt(Floor floor, Vector3 point, Vector2 origin, int nx, float step)
    {
        int x = Mathf.FloorToInt((point.x - origin.x) / step), z = Mathf.FloorToInt((point.z - origin.y) / step);
        for (int i = 0; i < floor.rectangles.Count; i++)
            if (floor.rectangles[i].Contains(new Vector2Int(x, z))) return floor.plateHeights[i];
        return floor.top;
    }

    private static void ConfigurePlayer(Scene scene, GameObject output)
    {
        OVRCameraRig rig = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            OVRCameraRig candidate = root.GetComponentInChildren<OVRCameraRig>(true);
            if (candidate == null) continue;
            if (rig != null) throw new InvalidOperationException("Hay mas de un OVRCameraRig en la escena.");
            rig = candidate;
        }
        if (rig == null) throw new InvalidOperationException("No se encontro el OVRCameraRig.");
        var character = rig.GetComponentInChildren<Oculus.Interaction.Locomotion.CharacterController>(true);
        var locomotor = rig.GetComponentInChildren<Oculus.Interaction.Locomotion.FirstPersonLocomotor>(true);
        CapsuleCollider capsule = character != null ? character.GetComponent<CapsuleCollider>() : null;
        if (character == null || locomotor == null || capsule == null)
            throw new InvalidOperationException("El rig no tiene el Character y FirstPersonLocomotor de Meta esperados.");
        Physics.SyncTransforms();
        Vector3 probe = rig.transform.position + Vector3.up * 0.5f;
        Ray ray = new Ray(probe, Vector3.down);
        RaycastHit nearest = default;
        float nearestDistance = float.PositiveInfinity;
        foreach (BoxCollider box in output.GetComponentsInChildren<BoxCollider>())
        {
            if (!box.transform.parent.name.StartsWith("Suelo_", StringComparison.Ordinal)) continue;
            if (box.Raycast(ray, out RaycastHit hit, 3f) && hit.normal.y > 0.9f && hit.distance < nearestDistance)
            { nearest = hit; nearestDistance = hit.distance; }
        }
        if (float.IsPositiveInfinity(nearestDistance))
            throw new InvalidOperationException("No hay piso de la cabana cerca del punto inicial del rig. Coloca su X/Z sobre la plataforma superior y repite.");
        Undo.RecordObject(rig.transform, "Colocar rig sobre el piso");
        Vector3 start = rig.transform.position; start.y = nearest.point.y + 0.015f;
        rig.transform.position = start;
        PrefabUtility.RecordPrefabInstancePropertyModifications(rig.transform);
        Undo.RecordObject(character, "Configurar colisiones de la cabana");
        character.LayerMask = character.LayerMask.value | (1 << output.layer);
        character.SkinWidth = Mathf.Max(character.SkinWidth, 0.01f);
        character.MaxReboundSteps = Mathf.Max(character.MaxReboundSteps, 6);
        PrefabUtility.RecordPrefabInstancePropertyModifications(character);
        GameObject spawn = Child(output, "Cabana_Spawn_Suelo");
        spawn.transform.position = start;
        ExperienceSpawn spawnController = rig.GetComponent<ExperienceSpawn>();
        if (spawnController == null) spawnController = Undo.AddComponent<ExperienceSpawn>(rig.gameObject);
        Undo.RecordObject(spawnController, "Asignar punto inicial de cabana");
        var serialized = new SerializedObject(spawnController);
        serialized.FindProperty("spawnPoint").objectReferenceValue = spawn.transform;
        serialized.FindProperty("locomotionRoot").objectReferenceValue = null;
        serialized.ApplyModifiedProperties();
        PrefabUtility.RecordPrefabInstancePropertyModifications(spawnController);
        CabinCollisionRecovery recovery = rig.GetComponent<CabinCollisionRecovery>();
        if (recovery == null) recovery = Undo.AddComponent<CabinCollisionRecovery>(rig.gameObject);
        Undo.RecordObject(recovery, "Asignar recuperacion de penetraciones");
        recovery.Configure(output.transform, capsule, character, locomotor);
        EditorUtility.SetDirty(recovery);
        Physics.SyncTransforms();
    }

    private static List<WallBox> BuildWalls(List<Triangle> triangles, Bounds bounds, float scale, float floorTop, float step)
    {
        var result = new List<WallBox>();
        float bottom = floorTop - 0.08f;
        float top = bounds.min.y + (5.9f - RawMinHeight) * scale;
        float binWidth = 0.12f * scale;
        for (int axis = 0; axis < 2; axis++)
        {
            int planeAxis = axis == 0 ? 0 : 2, alongAxis = axis == 0 ? 2 : 0;
            var groups = new Dictionary<int, List<Triangle>>();
            foreach (Triangle t in triangles)
            {
                Vector3 normal = Vector3.Cross(t.b - t.a, t.c - t.a).normalized;
                if (Mathf.Abs(normal.y) > 0.05f || Mathf.Abs(normal[planeAxis]) < 0.98f ||
                    t.center.y < bottom || t.center.y > top || t.area < 0.0001f) continue;
                int bin = Mathf.RoundToInt(t.center[planeAxis] / binWidth);
                if (!groups.TryGetValue(bin, out List<Triangle> group)) { group = new List<Triangle>(); groups.Add(bin, group); }
                group.Add(t);
            }
            var keys = new List<int>(groups.Keys); keys.Sort();
            var planes = new List<List<Triangle>>();
            List<Triangle> currentPlane = null;
            float planeStart = 0f;
            foreach (int key in keys)
            {
                float coordinate = key * binWidth;
                if (currentPlane == null || coordinate - planeStart > 0.30f * scale)
                {
                    currentPlane = new List<Triangle>(); planes.Add(currentPlane); planeStart = coordinate;
                }
                currentPlane.AddRange(groups[key]);
            }
            foreach (List<Triangle> group in planes)
            {
                float area = 0f, offset = 0f, minPlane = float.PositiveInfinity, maxPlane = float.NegativeInfinity;
                float minAlong = float.PositiveInfinity, maxAlong = float.NegativeInfinity;
                foreach (Triangle t in group)
                {
                    area += t.area; offset += t.center[planeAxis] * t.area;
                    foreach (Vector3 point in new[] { t.a, t.b, t.c })
                    {
                        minAlong = Mathf.Min(minAlong, point[alongAxis]); maxAlong = Mathf.Max(maxAlong, point[alongAxis]);
                        minPlane = Mathf.Min(minPlane, point[planeAxis]); maxPlane = Mathf.Max(maxPlane, point[planeAxis]);
                    }
                }
                if (area < 0.8f * scale * scale || maxAlong - minAlong < 0.35f * scale || maxPlane - minPlane > 0.3f) continue;
                offset /= area;
                Vector2 origin = new Vector2(minAlong - 2f * step, bottom - 2f * step);
                int nx = Mathf.CeilToInt((maxAlong - origin.x) / step) + 3;
                int nz = Mathf.CeilToInt((top - origin.y) / step) + 3;
                bool[] mask = new bool[nx * nz];
                foreach (Triangle t in group)
                {
                    // Reuse floor rasterization in a temporary plane: X = along, Z = height.
                    var projected = new Triangle(new Vector3(t.a[alongAxis], 0f, t.a.y),
                        new Vector3(t.b[alongAxis], 0f, t.b.y), new Vector3(t.c[alongAxis], 0f, t.c.y));
                    Rasterize(projected, mask, origin, nx, nz, step);
                }
                for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++)
                {
                    float y = origin.y + (z + 0.5f) * step;
                    if (y < bottom || y > top) mask[z * nx + x] = false;
                }
                bool[] dilation = new bool[mask.Length];
                for (int z = 1; z < nz - 1; z++) for (int x = 1; x < nx - 1; x++)
                    if (mask[z * nx + x]) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                        dilation[(z + dz) * nx + x + dx] = true;
                for (int z = 1; z < nz - 1; z++) for (int x = 1; x < nx - 1; x++)
                {
                    bool full = true;
                    for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) full &= dilation[(z + dz) * nx + x + dx];
                    mask[z * nx + x] |= full;
                }
                for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++)
                {
                    if (!mask[z * nx + x]) continue;
                    int width = 1;
                    while (x + width < nx && mask[z * nx + x + width]) width++;
                    int height = 1;
                    while (z + height < nz)
                    {
                        bool full = true; for (int dx = 0; dx < width; dx++) full &= mask[(z + height) * nx + x + dx];
                        if (!full) break; height++;
                    }
                    for (int dz = 0; dz < height; dz++) for (int dx = 0; dx < width; dx++) mask[(z + dz) * nx + x + dx] = false;
                    Vector3 center = Vector3.zero, size = Vector3.zero;
                    center[planeAxis] = offset; center[alongAxis] = origin.x + (x + width * 0.5f) * step;
                    center.y = origin.y + (z + height * 0.5f) * step;
                    size[planeAxis] = Mathf.Max(0.14f, maxPlane - minPlane + 0.04f);
                    size[alongAxis] = width * step + 0.004f; size.y = height * step + 0.004f;
                    result.Add(new WallBox { center = center, size = size });
                }
            }
        }
        return result;
    }

}
#endif
