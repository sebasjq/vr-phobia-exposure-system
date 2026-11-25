#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Editor tool for the static beach in Thalassophobia_1st_Level.
// Builds a closed height surface from the visible FBX, in world meters.
// The generated object uses only a MeshCollider; no runtime script is needed.
public sealed class BeachCollisionBuilder : EditorWindow
{
    private const string ModelSuffix = "/Talasofobia/Models/beach.fbx";
    private const string OutputName = "Beach_Collision_Surface";
    private const string OutputFolder = "Assets/GeneratedBeachCollision";
    private GameObject source;
    private float spacing = 0.12f;
    private float bottomDepth = 1f;

    private struct Edge
    {
        public int a;
        public int b;
        public Edge(int start, int end) { a = start; b = end; }
    }

    [MenuItem("Tools/Fobias/Playa/Generar superficie de colision")]
    private static void Open()
    {
        var window = GetWindow<BeachCollisionBuilder>("Colision de playa");
        window.source = Selection.activeGameObject;
        window.minSize = new Vector2(390f, 235f);
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Playa de Talasofobia 1", EditorStyles.boldLabel);
        source = (GameObject)EditorGUILayout.ObjectField("Objeto beach", source, typeof(GameObject), true);
        spacing = EditorGUILayout.Slider(new GUIContent("Separacion (m)",
            "0.12 m conserva el relieve y reduce el detalle de las colisiones."), spacing, 0.05f, 0.3f);
        bottomDepth = EditorGUILayout.Slider("Base bajo el modelo (m)", bottomDepth, 0.5f, 3f);
        EditorGUILayout.HelpBox(
            "Crea un collider cerrado con la superficie superior de la playa y las rocas. " +
            "Desactiva los MeshCollider originales de este modelo en esta escena. " +
            "La imagen del modelo se conserva. Puedes deshacer con Ctrl+Z.", MessageType.Info);
        EditorGUILayout.HelpBox(
            "Es una aproximacion de alturas: rellena el espacio bajo salientes de roca. " +
            "Se prepara para probar las colisiones de esta playa.", MessageType.None);
        using (new EditorGUI.DisabledScope(source == null || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            if (GUILayout.Button("Generar colision de la playa", GUILayout.Height(30)))
                Build();
        }
    }

    private void Build()
    {
        Mesh generatedMesh = null;
        GameObject generatedObject = null;
        string assetPath = null;
        int undoGroup = -1;
        try
        {
            if (source == null || EditorUtility.IsPersistent(source) || !source.scene.IsValid() ||
                !source.scene.isLoaded || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Selecciona beach en la jerarquia de la escena, fuera de Play y de Prefab Mode.");
            if (source.scene.name != "Thalassophobia_1st_Level")
                throw new InvalidOperationException("Abre Thalassophobia_1st_Level y selecciona su objeto beach.");
            foreach (GameObject root in source.scene.GetRootGameObjects())
                if (root.name == OutputName)
                    throw new InvalidOperationException("Ya existe Beach_Collision_Surface. Deshaz la generacion anterior antes de repetirla.");

            MeshFilter[] candidates = source.GetComponentsInChildren<MeshFilter>(false);
            var filters = new List<MeshFilter>();
            foreach (MeshFilter filter in candidates)
            {
                if (filter.sharedMesh == null) continue;
                string path = AssetDatabase.GetAssetPath(filter.sharedMesh).Replace('\\', '/');
                if (!path.EndsWith(ModelSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.enabled) filters.Add(filter);
            }
            if (filters.Count != 5)
                throw new InvalidOperationException("Selecciona el objeto beach completo: deben encontrarse sus cinco mallas visibles (Object_4 a Object_8).");

            var bounds = new Bounds();
            bool hasBounds = false;
            foreach (MeshFilter filter in filters)
            {
                Bounds current = filter.GetComponent<MeshRenderer>().bounds;
                if (!hasBounds) { bounds = current; hasBounds = true; }
                else bounds.Encapsulate(current);
            }
            float step = Mathf.Clamp(spacing, 0.05f, 0.3f);
            int nx = Mathf.CeilToInt(bounds.size.x / step) + 1;
            int nz = Mathf.CeilToInt(bounds.size.z / step) + 1;
            long gridCount = (long)nx * nz;
            if (nx < 2 || nz < 2 || gridCount > 400000)
                throw new InvalidOperationException("La rejilla es demasiado grande. Comprueba la escala del objeto o aumenta la separacion.");
            Vector3 origin = new Vector3(bounds.min.x, bounds.min.y, bounds.min.z);
            var heights = new float[(int)gridCount];
            for (int i = 0; i < heights.Length; i++) heights[i] = float.NegativeInfinity;

            for (int f = 0; f < filters.Count; f++)
            {
                MeshFilter filter = filters[f];
                Matrix4x4 matrix = filter.transform.localToWorldMatrix;
                // Editor-only API can read the imported mesh without enabling Read/Write.
                using (Mesh.MeshDataArray data = MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh))
                {
                    Mesh.MeshData meshData = data[0];
                    using (var vertices = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp))
                    {
                        meshData.GetVertices(vertices);
                        var world = new Vector3[vertices.Length];
                        for (int v = 0; v < vertices.Length; v++)
                            world[v] = matrix.MultiplyPoint3x4(vertices[v]) - origin;
                        for (int sub = 0; sub < meshData.subMeshCount; sub++)
                        {
                            SubMeshDescriptor descriptor = meshData.GetSubMesh(sub);
                            if (descriptor.topology != MeshTopology.Triangles) continue;
                            using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                            {
                                meshData.GetIndices(indices, sub, true);
                                for (int t = 0; t + 2 < indices.Length; t += 3)
                                {
                                    if (t % 15000 == 0 && EditorUtility.DisplayCancelableProgressBar(
                                        "Colision de playa", "Leyendo " + filter.name,
                                        (f + t / (float)Mathf.Max(1, indices.Length)) / filters.Count))
                                        throw new OperationCanceledException();
                                    Rasterize(world[indices[t]], world[indices[t + 1]], world[indices[t + 2]],
                                        step, nx, nz, heights);
                                }
                            }
                        }
                    }
                }
            }

            generatedMesh = CreateClosedMesh(heights, nx, nz, step, Mathf.Max(0.5f, bottomDepth));
            generatedMesh.name = "Beach_Collision_Surface";
            // All analysis finishes before changing the scene or disabling original colliders.
            if (!AssetDatabase.IsValidFolder(OutputFolder))
                AssetDatabase.CreateFolder("Assets", "GeneratedBeachCollision");
            assetPath = AssetDatabase.GenerateUniqueAssetPath(OutputFolder + "/Beach_Collision_Surface.asset");
            AssetDatabase.CreateAsset(generatedMesh, assetPath);
            AssetDatabase.SaveAssets();

            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generar colision de playa");
            generatedObject = new GameObject(OutputName);
            SceneManager.MoveGameObjectToScene(generatedObject, source.scene);
            Undo.RegisterCreatedObjectUndo(generatedObject, "Crear colision de playa");
            generatedObject.transform.SetPositionAndRotation(origin, Quaternion.identity);
            generatedObject.transform.localScale = Vector3.one;
            // Match the original layer so the existing Meta locomotor still detects it.
            generatedObject.layer = filters[0].gameObject.layer;
            var collider = Undo.AddComponent<MeshCollider>(generatedObject);
            collider.convex = false;
            collider.isTrigger = false;
            collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                MeshColliderCookingOptions.EnableMeshCleaning |
                MeshColliderCookingOptions.WeldColocatedVertices |
                MeshColliderCookingOptions.UseFastMidphase;
            collider.sharedMesh = generatedMesh;

            int disabled = 0;
            foreach (MeshCollider old in source.GetComponentsInChildren<MeshCollider>(true))
            {
                if (old.sharedMesh == null || !old.enabled) continue;
                string path = AssetDatabase.GetAssetPath(old.sharedMesh).Replace('\\', '/');
                if (!path.EndsWith(ModelSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                Undo.RecordObject(old, "Desactivar collider original de playa");
                old.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(old);
                disabled++;
            }
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(source.scene);
            Selection.activeGameObject = generatedObject;
            Debug.Log("Colision de playa creada: " + generatedMesh.vertexCount + " vertices, " +
                generatedMesh.triangles.Length / 3 + " triangulos; " + disabled +
                " colliders originales desactivados. Guarda la escena y prueba las rocas. " +
                "Esta generacion no equivale a una prueba en el visor.", generatedObject);
            // These now belong to the scene and asset database; do not destroy on success.
            generatedMesh = null;
            generatedObject = null;
            assetPath = null;
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Generacion cancelada. Los colliders originales se conservan.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Colision de playa", exception.Message, "Cerrar");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (generatedObject != null || assetPath != null)
            {
                if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
                if (generatedObject != null) DestroyImmediate(generatedObject);
                if (assetPath != null) AssetDatabase.DeleteAsset(assetPath);
            }
            if (generatedMesh != null && !EditorUtility.IsPersistent(generatedMesh))
                DestroyImmediate(generatedMesh);
        }
    }

    private static void Rasterize(Vector3 a, Vector3 b, Vector3 c,
        float step, int nx, int nz, float[] heights)
    {
        float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(denominator) < 1e-9f) return;
        int minX = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / step));
        int maxX = Mathf.Min(nx - 1, Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / step));
        int minZ = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / step));
        int maxZ = Mathf.Min(nz - 1, Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / step));
        const float tolerance = 0.00001f;
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float px = x * step;
            float pz = z * step;
            float u = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / denominator;
            float v = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / denominator;
            float w = 1f - u - v;
            if (u < -tolerance || v < -tolerance || w < -tolerance) continue;
            int index = z * nx + x;
            heights[index] = Mathf.Max(heights[index], u * a.y + v * b.y + w * c.y);
        }
    }

    private static Mesh CreateClosedMesh(float[] heights, int nx, int nz, float step, float depth)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var mapping = new int[heights.Length];
        for (int i = 0; i < mapping.Length; i++) mapping[i] = -1;
        var boundary = new Dictionary<ulong, Edge>();
        int Vertex(int grid)
        {
            if (mapping[grid] >= 0) return mapping[grid];
            mapping[grid] = vertices.Count;
            // Small positive offset keeps the feet just above the photographed surface.
            vertices.Add(new Vector3((grid % nx) * step, heights[grid] + 0.01f, (grid / nx) * step));
            return mapping[grid];
        }
        void TrackEdge(int a, int b)
        {
            ulong key = ((ulong)(uint)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            if (!boundary.Remove(key)) boundary.Add(key, new Edge(a, b));
        }
        void Top(int ga, int gb, int gc)
        {
            int a = Vertex(ga), b = Vertex(gb), c = Vertex(gc);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            TrackEdge(a, b); TrackEdge(b, c); TrackEdge(c, a);
        }
        for (int z = 0; z < nz - 1; z++)
        for (int x = 0; x < nx - 1; x++)
        {
            int a = z * nx + x, b = a + 1, c = a + nx, d = c + 1;
            if (float.IsNegativeInfinity(heights[a]) || float.IsNegativeInfinity(heights[b]) ||
                float.IsNegativeInfinity(heights[c]) || float.IsNegativeInfinity(heights[d])) continue;
            if (Mathf.Abs(heights[a] - heights[d]) <= Mathf.Abs(heights[b] - heights[c]))
            { Top(a, c, d); Top(a, d, b); }
            else { Top(a, c, b); Top(b, c, d); }
        }
        if (triangles.Count == 0)
            throw new InvalidOperationException("No se encontro una superficie de suelo. Comprueba el objeto beach.");
        int topVertices = vertices.Count;
        int topIndices = triangles.Count;
        for (int i = 0; i < topVertices; i++)
        {
            Vector3 p = vertices[i];
            vertices.Add(new Vector3(p.x, -depth, p.z));
        }
        for (int t = 0; t < topIndices; t += 3)
        {
            triangles.Add(triangles[t] + topVertices);
            triangles.Add(triangles[t + 2] + topVertices);
            triangles.Add(triangles[t + 1] + topVertices);
        }
        foreach (Edge edge in boundary.Values)
        {
            triangles.Add(edge.a); triangles.Add(edge.a + topVertices); triangles.Add(edge.b);
            triangles.Add(edge.b); triangles.Add(edge.a + topVertices); triangles.Add(edge.b + topVertices);
        }
        var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}
#endif
