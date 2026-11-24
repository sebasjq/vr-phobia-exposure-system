#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Herramienta de editor: guardar en Assets/Editor/CaveColliderGenerator.cs.
/// Seleccionar Entorno_Caverna en la Hierarchy y ejecutar el menu Tools.
/// </summary>
public static class CaveColliderGenerator
{
    private const string MenuPath = "Tools/Claustrofobia/Generar colliders en seleccion";
    private const string UndoName = "Generar colliders de caverna";

    [MenuItem(MenuPath, true)]
    private static bool CanGenerate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return false;

        foreach (GameObject root in Selection.gameObjects)
        {
            if (IsSceneObject(root))
                return true;
        }

        return false;
    }

    [MenuItem(MenuPath)]
    private static void Generate()
    {
        if (!CanGenerate())
            return;

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
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }

        Debug.Log(
            $"Colliders de caverna: {created} creados, {repaired} completados, " +
            $"{preserved} existentes conservados, {removed} vacios eliminados. " +
            $"Mallas con Rigidbody dinamico omitidas: {dynamicMeshes}. " +
            "Guarda la escena con Ctrl+S. Puedes deshacer con Ctrl+Z.",
            roots[0]);
    }

    private static bool IsSceneObject(GameObject obj)
    {
        return obj != null && !EditorUtility.IsPersistent(obj)
            && obj.scene.IsValid() && obj.scene.isLoaded;
    }
}
#endif
