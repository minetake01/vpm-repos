using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Minetake.VRChat.HandTrackingMode.Editor
{
    internal static class HandTrackingModePrefabGenerator
    {
        private const string MenuPath = "GameObject/VRChat/Generate Hand Tracking Mode Prefab";
        private const string ParameterName = "HandTrackingMode";
        private const string MenuLabel = "Hand Tracking";

        [MenuItem(MenuPath, false, 49)]
        private static void GenerateFromSelection(MenuCommand command)
        {
            var selected = command.context as GameObject ?? Selection.activeGameObject;
            var descriptor = FindAvatarDescriptor(selected);
            if (descriptor == null)
            {
                EditorUtility.DisplayDialog(
                    "Hand Tracking Mode",
                    "VRCAvatarDescriptorを持つアバター、またはその子を選択してください。",
                    "OK");
                return;
            }

            var defaultName = descriptor.gameObject.name + "_HandTrackingMode";
            var prefabPath = EditorUtility.SaveFilePanelInProject(
                "Hand Tracking Mode Prefabを保存",
                defaultName,
                "prefab",
                "生成するprefabの保存先を選択してください。");
            if (string.IsNullOrEmpty(prefabPath)) return;

            if (AssetDatabase.LoadAssetAtPath<Object>(prefabPath) != null)
            {
                EditorUtility.DisplayDialog(
                    "Hand Tracking Mode",
                    "既存アセットは上書きしません。別の保存先を選択してください。",
                    "OK");
                return;
            }

            try
            {
                var prefab = Generate(descriptor, prefabPath);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "Hand Tracking Mode",
                    "prefabを生成できませんでした。Consoleを確認してください。",
                    "OK");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateSelection()
        {
            return FindAvatarDescriptor(Selection.activeGameObject) != null;
        }

        internal static GameObject Generate(VRCAvatarDescriptor descriptor, string prefabPath)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(prefabPath)) throw new ArgumentException("Prefab path is empty.", nameof(prefabPath));
            if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Prefab path must be an Assets/*.prefab path.", nameof(prefabPath));

            var parentPath = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            var prefabName = Path.GetFileNameWithoutExtension(prefabPath);
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(prefabName))
                throw new ArgumentException("Prefab path is invalid.", nameof(prefabPath));

            var assetFolderName = prefabName + "_Assets";
            var assetFolderPath = parentPath + "/" + assetFolderName;
            if (AssetDatabase.IsValidFolder(assetFolderPath) || AssetDatabase.LoadAssetAtPath<Object>(prefabPath) != null)
                throw new InvalidOperationException("The prefab or its generated asset folder already exists.");

            var folderGuid = AssetDatabase.CreateFolder(parentPath, assetFolderName);
            if (string.IsNullOrEmpty(folderGuid))
                throw new InvalidOperationException("Failed to create the generated asset folder.");

            GameObject root = null;
            try
            {
                var replacements = CreateReplacementControllers(descriptor, assetFolderPath);
                var toggleController = CreateToggleController(assetFolderPath + "/HandTrackingMode.controller");

                root = new GameObject("Hand Tracking Mode");
                ConfigureParameters(root);
                ConfigureMenu(root);
                ConfigureMergeAnimator(root, toggleController, VRCAvatarDescriptor.AnimLayerType.FX, MergeAnimatorMode.Append);

                foreach (var replacement in replacements)
                {
                    ConfigureMergeAnimator(
                        root,
                        replacement.Value,
                        replacement.Key,
                        MergeAnimatorMode.Replace);
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null) throw new InvalidOperationException("PrefabUtility did not create a prefab.");

                AssetDatabase.SaveAssets();
                return prefab;
            }
            catch
            {
                AssetDatabase.DeleteAsset(prefabPath);
                AssetDatabase.DeleteAsset(assetFolderPath);
                throw;
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        private static VRCAvatarDescriptor FindAvatarDescriptor(GameObject selected)
        {
            if (selected == null) return null;
            return selected.GetComponent<VRCAvatarDescriptor>() ??
                   selected.GetComponentInParent<VRCAvatarDescriptor>(true);
        }

        private static Dictionary<VRCAvatarDescriptor.AnimLayerType, AnimatorController>
            CreateReplacementControllers(VRCAvatarDescriptor descriptor, string assetFolderPath)
        {
            var replacements = new Dictionary<VRCAvatarDescriptor.AnimLayerType, AnimatorController>();
            CopyAffectedControllers(descriptor.baseAnimationLayers, assetFolderPath, replacements);
            CopyAffectedControllers(descriptor.specialAnimationLayers, assetFolderPath, replacements);
            return replacements;
        }

        private static void CopyAffectedControllers(
            VRCAvatarDescriptor.CustomAnimLayer[] playableLayers,
            string assetFolderPath,
            IDictionary<VRCAvatarDescriptor.AnimLayerType, AnimatorController> replacements)
        {
            if (playableLayers == null) return;

            foreach (var playableLayer in playableLayers)
            {
                if (playableLayer.isDefault ||
                    playableLayer.animatorController is not AnimatorController source ||
                    replacements.ContainsKey(playableLayer.type) ||
                    !HasFingerTrackingControl(source))
                    continue;

                var sourcePath = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(sourcePath)) continue;

                var destinationPath = assetFolderPath + "/" + playableLayer.type + ".controller";
                if (!AssetDatabase.CopyAsset(sourcePath, destinationPath)) continue;

                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
                var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(destinationPath);
                if (copy == null)
                {
                    AssetDatabase.DeleteAsset(destinationPath);
                    continue;
                }

                NeutralizeFingerTrackingControls(copy);
                replacements.Add(playableLayer.type, copy);
            }
        }

        private static bool HasFingerTrackingControl(AnimatorController controller)
        {
            var noChange = VRC_AnimatorTrackingControl.TrackingType.NoChange;
            foreach (var layer in controller.layers)
            {
                foreach (var tracking in TrackingControls(layer.stateMachine))
                {
                    if (tracking.trackingLeftFingers != noChange ||
                        tracking.trackingRightFingers != noChange)
                        return true;
                }
            }

            return false;
        }

        private static void NeutralizeFingerTrackingControls(AnimatorController controller)
        {
            var noChange = VRC_AnimatorTrackingControl.TrackingType.NoChange;
            foreach (var layer in controller.layers)
            {
                foreach (var tracking in TrackingControls(layer.stateMachine))
                {
                    tracking.trackingLeftFingers = noChange;
                    tracking.trackingRightFingers = noChange;
                    EditorUtility.SetDirty(tracking);
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
        }

        private static IEnumerable<VRCAnimatorTrackingControl> TrackingControls(AnimatorStateMachine stateMachine)
        {
            if (stateMachine == null) yield break;

            foreach (var behaviour in stateMachine.behaviours)
            {
                if (behaviour is VRCAnimatorTrackingControl tracking)
                    yield return tracking;
            }

            foreach (var childState in stateMachine.states)
            {
                foreach (var behaviour in childState.state.behaviours)
                {
                    if (behaviour is VRCAnimatorTrackingControl tracking)
                        yield return tracking;
                }
            }

            foreach (var childStateMachine in stateMachine.stateMachines)
            {
                foreach (var tracking in TrackingControls(childStateMachine.stateMachine))
                    yield return tracking;
            }
        }

        private static AnimatorController CreateToggleController(string path)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.name = Path.GetFileNameWithoutExtension(path);
            controller.AddParameter(ParameterName, AnimatorControllerParameterType.Bool);

            var layers = controller.layers;
            var layer = layers[0];
            layer.name = "Hand Tracking Mode";
            layer.defaultWeight = 1f;
            layers[0] = layer;
            controller.layers = layers;

            var stateMachine = layer.stateMachine;
            stateMachine.name = "Hand Tracking Mode";
            var normal = stateMachine.AddState("Animation");
            normal.writeDefaultValues = false;
            AddFingerTrackingControl(normal, VRC_AnimatorTrackingControl.TrackingType.Animation);

            var tracking = stateMachine.AddState("Tracking");
            tracking.writeDefaultValues = false;
            AddFingerTrackingControl(tracking, VRC_AnimatorTrackingControl.TrackingType.Tracking);
            stateMachine.defaultState = normal;

            var enable = normal.AddTransition(tracking);
            ConfigureTransition(enable, AnimatorConditionMode.If);
            var disable = tracking.AddTransition(normal);
            ConfigureTransition(disable, AnimatorConditionMode.IfNot);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            return controller;
        }

        private static void AddFingerTrackingControl(
            AnimatorState state,
            VRC_AnimatorTrackingControl.TrackingType trackingType)
        {
            var control = state.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
            control.trackingLeftFingers = trackingType;
            control.trackingRightFingers = trackingType;
            control.debugString = "net.minetake.hand-tracking-mode";
        }

        private static void ConfigureTransition(AnimatorStateTransition transition, AnimatorConditionMode mode)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(mode, 0f, ParameterName);
        }

        private static void ConfigureParameters(GameObject root)
        {
            var parameters = root.AddComponent<ModularAvatarParameters>();
            parameters.parameters.Add(new ParameterConfig
            {
                nameOrPrefix = ParameterName,
                remapTo = string.Empty,
                internalParameter = false,
                isPrefix = false,
                syncType = ParameterSyncType.Bool,
                localOnly = false,
                defaultValue = 0f,
                saved = false,
                hasExplicitDefaultValue = true
            });
        }

        private static void ConfigureMenu(GameObject root)
        {
            root.AddComponent<ModularAvatarMenuInstaller>();

            var item = root.AddComponent<ModularAvatarMenuItem>();
            item.label = MenuLabel;
            item.isSynced = true;
            item.isSaved = false;
            item.isDefault = false;
            item.automaticValue = false;
            item.Control = new VRCExpressionsMenu.Control
            {
                name = MenuLabel,
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = ParameterName },
                value = 1f,
                subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
                labels = Array.Empty<VRCExpressionsMenu.Control.Label>()
            };
        }

        private static void ConfigureMergeAnimator(
            GameObject root,
            RuntimeAnimatorController controller,
            VRCAvatarDescriptor.AnimLayerType layerType,
            MergeAnimatorMode mode)
        {
            var merge = root.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.layerType = layerType;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;
            merge.deleteAttachedAnimator = false;
            merge.layerPriority = 0;
            merge.mergeAnimatorMode = mode;
        }
    }
}
