#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Herramienta de editor: guardar en Assets/Editor/CaveColliderGenerator.cs.
/// Seleccionar Entorno_Caverna en la Hierarchy y ejecutar el menu Tools.
/// </summary>
public static class CaveColliderGenerator
{
    private const string MenuPath = "Tools/Claustrofobia/Generar colliders en seleccion";
    private const string UndoName = "Generar colliders de caverna";

    [MenuItem(MenuPath)]
    private static void Generate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Colliders de caverna",
                "Sal de Play antes de generar los colliders.", "Aceptar");
            return;
        }

        var roots = new List<GameObject>();
        var filters = new HashSet<MeshFilter>();
        foreach (GameObject root in Selection.gameObjects)
        {
            if (!IsSceneObject(root))
                continue;

            roots.Add(root);
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                filters.Add(filter);
        }

        if (roots.Count == 0)
        {
            EditorUtility.DisplayDialog("Selecciona la caverna",
                "Selecciona Entorno_Caverna en la Hierarchy, no el archivo del modelo en Project.",
                "Aceptar");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);

        int created = 0;
        int repaired = 0;
        int preserved = 0;
        int removed = 0;
        int dynamicMeshes = 0;

        try
        {
            foreach (MeshFilter filter in filters)
            {
                if (filter == null || filter.sharedMesh == null)
                    continue;

                GameObject meshObject = filter.gameObject;
                Rigidbody body = meshObject.GetComponentInParent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    dynamicMeshes++;
                    continue;
                }

                MeshCollider target = null;
                bool hasValidCollider = false;
                foreach (MeshCollider existing in meshObject.GetComponents<MeshCollider>())
                {
                    if (existing.sharedMesh != null)
                        hasValidCollider = true;
                    else if (target == null)
                        target = existing;
                }

                // Conservar los colliders que ya tienen una malla, sin duplicarlos.
                if (hasValidCollider)
                {
                    preserved++;
                    continue;
                }

                bool isNew = target == null;
                if (isNew)
                    target = Undo.AddComponent<MeshCollider>(meshObject);

                Undo.RecordObject(target, UndoName);
                target.isTrigger = false;
                target.convex = false;
                target.sharedMesh = filter.sharedMesh;
                target.enabled = true;

                if (PrefabUtility.IsPartOfPrefabInstance(target))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);

                EditorSceneManager.MarkSceneDirty(meshObject.scene);
                if (isNew)
                    created++;
                else
                    repaired++;
            }

            // Quitar tambien los colliders vacios creados antes en contenedores.
            var inspected = new HashSet<MeshCollider>();
            foreach (GameObject root in roots)
            {
                foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (collider == null || !inspected.Add(collider) || collider.sharedMesh != null)
                        continue;

                    GameObject owner = collider.gameObject;
                    Undo.DestroyObjectImmediate(collider);
                    EditorSceneManager.MarkSceneDirty(owner.scene);
                    removed++;
                }
            }
        }
        catch (Exception exception)
        {
            EditorUtility.DisplayDialog("Generacion interrumpida",
                "No se completo la generacion. Los cambios parciales se pueden deshacer con Ctrl+Z.\n\n"
                + exception.Message, "Aceptar");
            Debug.LogException(exception);
            return;
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }

        string summary = $"Escena: {roots[0].scene.name}\nObjeto seleccionado: {roots[0].name}\n\n"
            + $"Creados: {created} | Completados: {repaired}\n"
            + $"Existentes conservados: {preserved} | Vacios eliminados: {removed}\n"
            + $"Mallas con Rigidbody dinamico omitidas: {dynamicMeshes}\n\n"
            + BuildGroundReport(roots[0].scene)
            + "\n\nGuarda con Ctrl+S. Sal de Play y vuelve a entrar tras ajustar la escena."
            + "\nLa generacion se puede deshacer con Ctrl+Z.";
        Debug.Log(summary, roots[0]);
        EditorUtility.DisplayDialog("Resultado: colliders y suelo", summary, "Aceptar");
    }

    [MenuItem("Tools/Claustrofobia/Revisar suelo del OVR")]
    private static void ReviewGround()
    {
        Scene scene = IsSceneObject(Selection.activeGameObject)
            ? Selection.activeGameObject.scene : SceneManager.GetActiveScene();
        EditorUtility.DisplayDialog("Suelo del OVR", BuildGroundReport(scene), "Aceptar");
    }

    private static string BuildGroundReport(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return "No hay una escena cargada.";

        GameObject rig = null;
        int rigCount = 0;
        int validColliders = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.sharedMesh != null && collider.enabled && collider.gameObject.activeInHierarchy)
                    validColliders++;
            }
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component != null && component.GetType().Name == "OVRCameraRig"
                    && component.isActiveAndEnabled)
                {
                    rig = component.gameObject;
                    rigCount++;
                }
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"Escena: {scene.name}");
        report.AppendLine($"Mesh Colliders activos con malla: {validColliders}");
        if (rigCount != 1)
        {
            report.AppendLine($"OVR activos encontrados: {rigCount}. Debe haber uno.");
            return report.ToString();
        }

        Component locomotor = null;
        foreach (MonoBehaviour component in rig.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component != null && component.GetType().Name == "FirstPersonLocomotor")
            {
                locomotor = component;
                break;
            }
        }

        if (locomotor == null)
        {
            report.AppendLine("No encontre FirstPersonLocomotor dentro del OVR. Revisa el Locomotor.");
            return report.ToString();
        }

        var settings = new SerializedObject(locomotor);
        SerializedProperty disabled = settings.FindProperty("_velocityDisabled");
        SerializedProperty distance = settings.FindProperty("_maxStartGroundDistance");
        SerializedProperty controllerRef = settings.FindProperty("_characterController");
        float limit = distance != null ? distance.floatValue : 10f;
        float maxSlope = 50f;
        int mask = Physics.DefaultRaycastLayers;
        if (controllerRef != null && controllerRef.objectReferenceValue != null)
        {
            var controller = new SerializedObject(controllerRef.objectReferenceValue);
            SerializedProperty layers = controller.FindProperty("_layerMask");
            SerializedProperty slope = controller.FindProperty("_maxSlopeAngle");
            if (layers != null) mask = layers.intValue;
            if (slope != null) maxSlope = slope.floatValue;
        }

        if (locomotor is Behaviour behaviour && !behaviour.isActiveAndEnabled)
            report.AppendLine("El FirstPersonLocomotor esta desactivado.");
        if (disabled != null)
            report.AppendLine("Velocity Disabled: " + (disabled.boolValue ? "SI" : "NO"));
        report.AppendLine($"Posicion del OVR: {rig.transform.position:F3}");
        report.AppendLine($"Limite de busqueda inicial: {limit:F2} m");
        report.AppendLine("Comprobacion orientativa: rayo vertical bajo la raiz del OVR.");

        Physics.SyncTransforms();
        Vector3 origin = rig.transform.position + Vector3.up * 0.1f;
        float checkDistance = limit >= 0f ? limit + 0.1f : 100f;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, checkDistance,
            mask, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (first, second) => first.distance.CompareTo(second.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.gameObject.scene != scene
                || hit.collider.transform.IsChildOf(rig.transform))
                continue;

            float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
            report.AppendLine($"Superficie: {hit.collider.name}");
            report.AppendLine($"Capa: {LayerMask.LayerToName(hit.collider.gameObject.layer)}");
            report.AppendLine($"Separacion vertical: {hit.point.y - rig.transform.position.y:F3} m");
            report.AppendLine($"Inclinacion: {slopeAngle:F1} grados (limite: {maxSlope:F1})");
            report.AppendLine(slopeAngle <= maxSlope
                ? "Hay una superficie transitable debajo. Si no avanza, revisa controles y el estado en Play."
                : "La superficie es demasiado inclinada para caminar. Revisa la rotacion del modelo o el punto de aparicion.");
            return report.ToString();
        }

        report.AppendLine("NO detecte suelo bajo el OVR con la mascara del controlador.");
        report.AppendLine("Revisa la posicion inicial, los colliders y Layer Mask.");
        return report.ToString();
    }

    [MenuItem("Tools/Claustrofobia/Corregir cara interior del suelo")]
    private static void FixInteriorFloor()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Corregir suelo", "Sal de Play antes de corregir el suelo.", "Aceptar");
            return;
        }

        GameObject environment = Selection.activeGameObject;
        if (!IsSceneObject(environment))
        {
            EditorUtility.DisplayDialog("Corregir suelo",
                "Selecciona Entorno_Caverna en la Hierarchy.", "Aceptar");
            return;
        }

        try
        {
            GameObject rig = null;
            foreach (GameObject root in environment.scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component != null && component.GetType().Name == "OVRCameraRig"
                        && component.isActiveAndEnabled)
                        rig = component.gameObject;
                }
            }
            if (rig == null)
                throw new InvalidOperationException("No encontre un OVRCameraRig activo en esta escena.");

            int mask = Physics.DefaultRaycastLayers;
            float distance = 10f;
            foreach (MonoBehaviour component in rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null || component.GetType().Name != "FirstPersonLocomotor")
                    continue;

                var settings = new SerializedObject(component);
                SerializedProperty limit = settings.FindProperty("_maxStartGroundDistance");
                if (limit != null)
                    distance = limit.floatValue >= 0f ? limit.floatValue : 100f;
                SerializedProperty controllerRef = settings.FindProperty("_characterController");
                if (controllerRef != null && controllerRef.objectReferenceValue != null)
                {
                    var controller = new SerializedObject(controllerRef.objectReferenceValue);
                    SerializedProperty layers = controller.FindProperty("_layerMask");
                    if (layers != null) mask = layers.intValue;
                }
                break;
            }

            Physics.SyncTransforms();
            bool previousBackfaceSetting = Physics.queriesHitBackfaces;
            RaycastHit[] hits;
            try
            {
                // Consultar el reverso solo durante esta busqueda y restaurar la configuracion.
                Physics.queriesHitBackfaces = true;
                hits = Physics.RaycastAll(rig.transform.position + Vector3.up * 0.1f,
                    Vector3.down, distance + 0.1f, mask, QueryTriggerInteraction.Ignore);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousBackfaceSetting;
            }

            Array.Sort(hits, (first, second) => first.distance.CompareTo(second.distance));
            MeshCollider target = null;
            RaycastHit floorHit = default;
            foreach (RaycastHit hit in hits)
            {
                var candidate = hit.collider as MeshCollider;
                if (candidate == null || candidate.sharedMesh == null
                    || candidate.gameObject.scene != environment.scene
                    || !candidate.transform.IsChildOf(environment.transform))
                    continue;

                target = candidate;
                floorHit = hit;
                break;
            }
            if (target == null)
                throw new InvalidOperationException(
                    "No encontre una malla de suelo bajo el OVR, tampoco mirando las caras traseras. "
                    + "Comprueba que la caverna seleccionada tenga colliders, que su capa este incluida "
                    + "en Layer Mask y que el OVR este situado sobre su suelo.");

            Mesh source = target.sharedMesh;
            int[] indices = source.triangles;
            int triangle = floorHit.triangleIndex * 3;
            if (triangle < 0 || triangle + 2 >= indices.Length)
                throw new InvalidOperationException("No pude identificar el triangulo del suelo.");

            Vector3[] vertices = source.vertices;
            Vector3 a = target.transform.TransformPoint(vertices[indices[triangle]]);
            Vector3 b = target.transform.TransformPoint(vertices[indices[triangle + 1]]);
            Vector3 c = target.transform.TransformPoint(vertices[indices[triangle + 2]]);
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (normal.y > -0.5f)
            {
                EditorUtility.DisplayDialog("Orientacion del suelo",
                    $"Superficie: {target.name}\nNormal del triangulo: {normal:F3}\n\n"
                    + "Esta superficie no apunta claramente hacia abajo; se conserva su malla.\n\n"
                    + BuildGroundReport(environment.scene), "Aceptar");
                return;
            }

            // Una copia exclusiva para colision: el MeshFilter y los materiales conservan su malla.
            Mesh interiorMesh = UnityEngine.Object.Instantiate(source);
            interiorMesh.name = source.name + "_InteriorCollider";
            for (int subMesh = 0; subMesh < interiorMesh.subMeshCount; subMesh++)
            {
                if (interiorMesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    continue;

                int[] triangles = interiorMesh.GetTriangles(subMesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int swap = triangles[i + 1];
                    triangles[i + 1] = triangles[i + 2];
                    triangles[i + 2] = swap;
                }
                interiorMesh.SetTriangles(triangles, subMesh, false);
            }

            const string folder = "Assets/GeneratedCaveColliders";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "GeneratedCaveColliders");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/InteriorCollider.asset");
            AssetDatabase.CreateAsset(interiorMesh, assetPath);

            Undo.RecordObject(target, "Corregir colision interior de caverna");
            target.sharedMesh = null;
            target.sharedMesh = interiorMesh;
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
            AssetDatabase.SaveAssets();
            Physics.SyncTransforms();

            EditorUtility.DisplayDialog("Colision interior corregida",
                $"Collider corregido: {target.name}\nMalla de colision guardada: {assetPath}\n\n"
                + BuildGroundReport(environment.scene)
                + "\n\nGuarda la escena con Ctrl+S y reinicia Play. Incluye la carpeta "
                + "GeneratedCaveColliders en Git junto con la escena. Ctrl+Z restaura el collider anterior.",
                "Aceptar");
        }
        catch (Exception exception)
        {
            EditorUtility.DisplayDialog("No se completo la correccion", exception.Message, "Aceptar");
            Debug.LogException(exception);
        }
    }

    private static bool IsSceneObject(GameObject obj)
    {
        return obj != null && !EditorUtility.IsPersistent(obj)
            && obj.scene.IsValid() && obj.scene.isLoaded;
    }
}
#endif
