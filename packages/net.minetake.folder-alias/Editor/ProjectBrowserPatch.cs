using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Minetake.FolderAlias.Editor
{
    internal static class ProjectBrowserPatch
    {
        private const string SupportedUnityVersion = "2022.3.22f1";
        private const string HarmonyId = "net.minetake.folder-alias";
        private static readonly Color DisplayNameColor = Color.yellow;

        private static Type assetsTreeViewGuiType;
        private static Type assetsTreeViewDataSourceType;
        private static Type projectBrowserColumnOneTreeViewDataSourceType;
        private static Type folderTreeItemBaseType;
        private static Type objectListAreaType;
        private static Type filterResultType;
        private static Type renameOverlayType;
        private static MethodInfo treeGetRenameOverlay;
        private static MethodInfo listGetRenameOverlay;
        private static MethodInfo listClearRenameState;
        private static MethodInfo getAllProjectBrowsers;
        private static MethodInfo treeGetVisibleItemsRecursive;
        private static MethodInfo resetProjectBrowserViews;
        private static FieldInfo treeRootItemField;
        private static FieldInfo treeRowsField;
        private static FieldInfo treeGuiControllerField;
        private static FieldInfo filterResultNameField;
        private static PropertyInfo filterResultGuidProperty;
        private static PropertyInfo renameNameProperty;
        private static PropertyInfo renameOriginalNameProperty;
        private static PropertyInfo renameAcceptedProperty;
        private static PropertyInfo renameUserDataProperty;
        private static FieldInfo treeLineStyleField;
        private static FieldInfo treeBoldLineStyleField;
        private static FieldInfo listLabelStyleField;
        private static FieldInfo gridLabelStyleField;
        private static MethodInfo listDrawItemMethod;
        private static PropertyInfo treeFoldersFirstProperty;
        private static PropertyInfo treeControllerDataProperty;
        private static PropertyInfo treeIsSearchingProperty;
        private static Harmony harmony;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            if (!string.Equals(Application.unityVersion, SupportedUnityVersion, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Folder Alias supports Unity {SupportedUnityVersion} only. " +
                    $"Current version: {Application.unityVersion}.");
            }

            ResolveAndValidateContracts();

            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                RequireMethod(renameOverlayType, "BeginRename", typeof(string), typeof(int), typeof(float)),
                prefix: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(BeforeBeginRename)));

            var renamePrefix = new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(BeforeRenameEnded));
            var renamePostfix = new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(AfterRenameEnded));
            harmony.Patch(RequireMethod(assetsTreeViewGuiType, "RenameEnded"), renamePrefix, renamePostfix);
            harmony.Patch(RequireMethod(objectListAreaType, "RenameEnded"), renamePrefix, renamePostfix);

            harmony.Patch(
                RequireMethod(assetsTreeViewGuiType, "BeginRowGUI"),
                prefix: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(BeforeTreeRowsDrawn)));

            var filteredHierarchyComparerType = RequireType("UnityEditor.FilteredHierarchy+<>c");
            var filterResultComparerPrefix = new HarmonyMethod(
                typeof(ProjectBrowserPatch),
                nameof(BeforeFilterResultCompare));
            harmony.Patch(
                RequireMethod(filteredHierarchyComparerType, "<FolderBrowsing>b__27_0", filterResultType, filterResultType),
                prefix: filterResultComparerPrefix);
            harmony.Patch(
                RequireMethod(filteredHierarchyComparerType, "<FolderBrowsing>b__27_1", filterResultType, filterResultType),
                prefix: filterResultComparerPrefix);
            harmony.Patch(
                RequireMethod(filteredHierarchyComparerType, "<ResultsChanged>b__29_0", filterResultType, filterResultType),
                prefix: filterResultComparerPrefix);

            harmony.Patch(
                RequireMethod(
                    assetsTreeViewGuiType,
                    "DoItemGUI",
                    typeof(Rect),
                    typeof(int),
                    typeof(TreeViewItem),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool)),
                prefix: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(BeforeTreeItemDraw)),
                finalizer: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(AfterTreeItemDraw)));

            EditorApplication.projectWindowItemOnGUI += InstallListDrawPatch;
        }

        private static void InstallListDrawPatch(string guid, Rect selectionRect)
        {
            EditorApplication.projectWindowItemOnGUI -= InstallListDrawPatch;
            harmony.Patch(
                listDrawItemMethod,
                prefix: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(BeforeListItemDraw)),
                finalizer: new HarmonyMethod(typeof(ProjectBrowserPatch), nameof(AfterListItemDraw)));
            EditorApplication.RepaintProjectWindow();
        }

        internal static void RefreshProjectBrowsers()
        {
            if (getAllProjectBrowsers == null)
                throw new InvalidOperationException("ProjectBrowser patch is not initialized.");

            var browsers = getAllProjectBrowsers.Invoke(null, null) as IEnumerable;
            if (browsers == null)
                throw new InvalidOperationException("Unity returned no ProjectBrowser collection.");

            foreach (var browser in browsers)
            {
                resetProjectBrowserViews.Invoke(browser, null);
                if (browser is EditorWindow window) window.Repaint();
            }
        }

        private static void BeforeTreeRowsDrawn(object __instance)
        {
            var treeController = treeGuiControllerField.GetValue(__instance)
                                 ?? throw new InvalidOperationException("Unity returned no TreeViewController instance.");
            if ((bool)treeIsSearchingProperty.GetValue(treeController)) return;

            var dataSource = treeControllerDataProperty.GetValue(treeController)
                             ?? throw new InvalidOperationException("Unity returned no TreeViewDataSource instance.");
            var isAssetsTree = assetsTreeViewDataSourceType.IsInstanceOfType(dataSource);
            var isColumnOneTree = projectBrowserColumnOneTreeViewDataSourceType.IsInstanceOfType(dataSource);
            if (!isAssetsTree && !isColumnOneTree) return;

            var root = treeRootItemField.GetValue(dataSource) as TreeViewItem
                       ?? throw new InvalidOperationException("Unity returned no Project Browser tree root item.");
            var foldersFirst = isColumnOneTree || (bool)treeFoldersFirstProperty.GetValue(dataSource);
            if (!SortTreeChildrenByDisplayName(root, foldersFirst)) return;

            var rows = treeRowsField.GetValue(dataSource) as System.Collections.Generic.IList<TreeViewItem>
                       ?? throw new InvalidOperationException("Unity returned no Project Browser tree row collection.");
            rows.Clear();
            treeGetVisibleItemsRecursive.Invoke(dataSource, new object[] { root, rows });
        }

        private static bool SortTreeChildrenByDisplayName(TreeViewItem parent, bool foldersFirst)
        {
            if (parent.children == null) return false;

            var orderChanged = false;
            var hasAliasedChild = parent.children.Exists(
                child => child != null && TryGetTreeSortName(child, out _));
            if (hasAliasedChild)
            {
                var children = parent.children.ToArray();
                Array.Sort(children, (left, right) => CompareTreeItems(left, right, foldersFirst));
                for (var index = 0; index < children.Length; index++)
                {
                    if (ReferenceEquals(children[index], parent.children[index])) continue;
                    orderChanged = true;
                    break;
                }

                if (orderChanged)
                    parent.children = new System.Collections.Generic.List<TreeViewItem>(children);
            }

            foreach (var child in parent.children)
            {
                if (child != null && SortTreeChildrenByDisplayName(child, foldersFirst))
                    orderChanged = true;
            }

            return orderChanged;
        }

        private static int CompareTreeItems(TreeViewItem left, TreeViewItem right, bool foldersFirst)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            if (foldersFirst)
            {
                var leftIsFolder = folderTreeItemBaseType.IsInstanceOfType(left);
                var rightIsFolder = folderTreeItemBaseType.IsInstanceOfType(right);
                if (leftIsFolder != rightIsFolder) return leftIsFolder ? -1 : 1;
            }

            var leftName = TryGetTreeSortName(left, out var leftDisplayName) ? leftDisplayName : left.displayName;
            var rightName = TryGetTreeSortName(right, out var rightDisplayName) ? rightDisplayName : right.displayName;
            var comparison = EditorUtility.NaturalCompare(leftName, rightName);
            return comparison != 0 ? comparison : EditorUtility.NaturalCompare(left.displayName, right.displayName);
        }

        private static bool TryGetTreeSortName(TreeViewItem item, out string displayName)
        {
            var path = AssetDatabase.GetAssetPath(item.id);
            if (!FolderAliasSettings.IsAssetFolderPath(path))
            {
                displayName = null;
                return false;
            }

            return FolderAliasSettings.instance.TryGetDisplayName(
                AssetDatabase.AssetPathToGUID(path),
                out displayName);
        }

        private static bool BeforeFilterResultCompare(object result1, object result2, ref int __result)
        {
            var hasLeftDisplayName = TryGetFilterResultSortName(result1, out var leftDisplayName);
            var hasRightDisplayName = TryGetFilterResultSortName(result2, out var rightDisplayName);
            if (!hasLeftDisplayName && !hasRightDisplayName) return true;

            var leftActualName = filterResultNameField.GetValue(result1) as string;
            var rightActualName = filterResultNameField.GetValue(result2) as string;
            var comparison = EditorUtility.NaturalCompare(
                hasLeftDisplayName ? leftDisplayName : leftActualName,
                hasRightDisplayName ? rightDisplayName : rightActualName);
            __result = comparison != 0
                ? comparison
                : EditorUtility.NaturalCompare(leftActualName, rightActualName);
            return false;
        }

        private static bool TryGetFilterResultSortName(object filterResult, out string displayName)
        {
            if (filterResult == null)
            {
                displayName = null;
                return false;
            }

            var guid = filterResultGuidProperty.GetValue(filterResult) as string;
            return FolderAliasSettings.instance.TryGetDisplayName(guid, out displayName);
        }

        private static void BeforeBeginRename(ref string name, int userData)
        {
            var path = AssetDatabase.GetAssetPath(userData);
            if (!FolderAliasSettings.IsAssetFolderPath(path)) return;

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (FolderAliasSettings.instance.TryGetDisplayName(guid, out var displayName))
                name = displayName;
        }

        private static bool BeforeRenameEnded(object __instance, out RenameDecisionState __state)
        {
            __state = default;
            var renameOverlay = GetRenameOverlay(__instance);
            if (!(bool)renameAcceptedProperty.GetValue(renameOverlay)) return true;

            var instanceId = (int)renameUserDataProperty.GetValue(renameOverlay);
            var path = AssetDatabase.GetAssetPath(instanceId);
            if (!FolderAliasSettings.IsAssetFolderPath(path)) return true;

            var guid = AssetDatabase.AssetPathToGUID(path);
            var enteredName = (string)renameNameProperty.GetValue(renameOverlay);
            if (string.IsNullOrWhiteSpace(enteredName)
                && FolderAliasSettings.instance.TryGetDisplayName(guid, out _))
            {
                FolderAliasSettings.instance.Remove(guid);
                ClearListRenameStateWhenNeeded(__instance);
                return false;
            }

            if (string.IsNullOrEmpty(enteredName))
                enteredName = (string)renameOriginalNameProperty.GetValue(renameOverlay);

            var actualName = Path.GetFileName(path);
            var choice = EditorUtility.DisplayDialogComplex(
                "フォルダ名の変更",
                $"入力した名前: {enteredName}\n実際のフォルダ名: {actualName}\n\nどちらを変更しますか？",
                "表示名のみ変更",
                "キャンセル",
                "フォルダ名を変更");

            switch (choice)
            {
                case 0:
                    FolderAliasSettings.instance.SetDisplayName(guid, enteredName);
                    ClearListRenameStateWhenNeeded(__instance);
                    return false;
                case 1:
                    ClearListRenameStateWhenNeeded(__instance);
                    return false;
                case 2:
                    __state = new RenameDecisionState(guid, enteredName);
                    return true;
                default:
                    throw new InvalidOperationException($"Unexpected folder rename choice: {choice}.");
            }
        }

        private static void AfterRenameEnded(RenameDecisionState __state)
        {
            if (!__state.RemoveDisplayNameAfterPhysicalRename) return;

            var path = AssetDatabase.GUIDToAssetPath(__state.Guid);
            if (!FolderAliasSettings.IsAssetFolderPath(path)) return;
            if (!string.Equals(Path.GetFileName(path), __state.ExpectedPhysicalName, StringComparison.Ordinal)) return;
            FolderAliasSettings.instance.Remove(__state.Guid);
        }

        private static void BeforeTreeItemDraw(TreeViewItem item, out TreeDrawState __state)
        {
            __state = default;
            var path = AssetDatabase.GetAssetPath(item.id);
            if (!FolderAliasSettings.IsAssetFolderPath(path)) return;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!FolderAliasSettings.instance.TryGetDisplayName(guid, out var displayName)) return;

            var lineStyle = GetRequiredStyle(treeLineStyleField);
            var boldLineStyle = GetRequiredStyle(treeBoldLineStyleField);
            __state = new TreeDrawState(item, item.displayName, lineStyle, boldLineStyle);
            item.displayName = displayName;
            __state.ApplyColor(DisplayNameColor);
        }

        private static Exception AfterTreeItemDraw(Exception __exception, TreeDrawState __state)
        {
            __state.Restore();
            return __exception;
        }

        private static void BeforeListItemDraw(object filterItem, out ListDrawState __state)
        {
            __state = default;
            if (filterItem == null) return;

            var guid = filterResultGuidProperty.GetValue(filterItem) as string;
            if (!FolderAliasSettings.instance.TryGetDisplayName(guid, out var displayName)) return;
            if (!FolderAliasSettings.IsAssetFolderGuid(guid)) return;

            var listStyle = GetRequiredStyle(listLabelStyleField);
            var gridStyle = GetRequiredStyle(gridLabelStyleField);
            var originalName = filterResultNameField.GetValue(filterItem) as string;
            __state = new ListDrawState(filterItem, originalName, listStyle, gridStyle);
            filterResultNameField.SetValue(filterItem, displayName);
            __state.ApplyColor(DisplayNameColor);
        }

        private static Exception AfterListItemDraw(Exception __exception, ListDrawState __state)
        {
            __state.Restore();
            return __exception;
        }

        private static object GetRenameOverlay(object instance)
        {
            var method = objectListAreaType.IsInstanceOfType(instance) ? listGetRenameOverlay : treeGetRenameOverlay;
            return method.Invoke(instance, null)
                   ?? throw new InvalidOperationException("Unity returned no RenameOverlay instance.");
        }

        private static void ClearListRenameStateWhenNeeded(object instance)
        {
            if (objectListAreaType.IsInstanceOfType(instance))
                listClearRenameState.Invoke(instance, null);
        }

        private static void ResolveAndValidateContracts()
        {
            assetsTreeViewGuiType = RequireType("UnityEditor.AssetsTreeViewGUI");
            assetsTreeViewDataSourceType = RequireType("UnityEditor.AssetsTreeViewDataSource");
            projectBrowserColumnOneTreeViewDataSourceType =
                RequireType("UnityEditor.ProjectBrowserColumnOneTreeViewDataSource");
            folderTreeItemBaseType = RequireType("UnityEditor.AssetsTreeViewDataSource+FolderTreeItemBase");
            objectListAreaType = RequireType("UnityEditor.ObjectListArea");
            filterResultType = RequireType("UnityEditor.FilteredHierarchy+FilterResult");
            renameOverlayType = RequireType("UnityEditor.RenameOverlay");

            treeGetRenameOverlay = RequireMethod(assetsTreeViewGuiType.BaseType, "GetRenameOverlay");
            listGetRenameOverlay = RequireMethod(objectListAreaType, "GetRenameOverlay");
            listClearRenameState = RequireMethod(objectListAreaType, "ClearRenameState");
            filterResultNameField = RequireField(filterResultType, "name");
            filterResultGuidProperty = RequireProperty(filterResultType, "guid");
            renameNameProperty = RequireProperty(renameOverlayType, "name");
            renameOriginalNameProperty = RequireProperty(renameOverlayType, "originalName");
            renameAcceptedProperty = RequireProperty(renameOverlayType, "userAcceptedRename");
            renameUserDataProperty = RequireProperty(renameOverlayType, "userData");
            treeFoldersFirstProperty = RequireProperty(assetsTreeViewDataSourceType, "foldersFirst");

            var treeViewDataSourceType = RequireType("UnityEditor.IMGUI.Controls.TreeViewDataSource");
            var treeViewControllerType = RequireType("UnityEditor.IMGUI.Controls.TreeViewController");
            var treeViewGuiType = RequireType("UnityEditor.IMGUI.Controls.TreeViewGUI");
            treeRootItemField = RequireField(treeViewDataSourceType, "m_RootItem");
            treeRowsField = RequireField(treeViewDataSourceType, "m_Rows");
            treeGuiControllerField = RequireField(treeViewGuiType, "m_TreeView");
            treeGetVisibleItemsRecursive = RequireMethod(
                treeViewDataSourceType,
                "GetVisibleItemsRecursive",
                typeof(TreeViewItem),
                typeof(System.Collections.Generic.IList<TreeViewItem>));
            treeControllerDataProperty = RequireProperty(treeViewControllerType, "data");
            treeIsSearchingProperty = RequireProperty(treeViewControllerType, "isSearching");

            var treeStylesType = RequireType("UnityEditor.IMGUI.Controls.TreeViewGUI+Styles");
            treeLineStyleField = RequireField(treeStylesType, "lineStyle");
            treeBoldLineStyleField = RequireField(treeStylesType, "lineBoldStyle");
            var listStylesType = RequireType("UnityEditor.ObjectListArea+Styles");
            listLabelStyleField = RequireField(listStylesType, "resultsLabel");
            gridLabelStyleField = RequireField(listStylesType, "resultsGridLabel");
            var localGroupType = RequireType("UnityEditor.ObjectListArea+LocalGroup");
            var builtinResourceType = RequireType("UnityEditor.BuiltinResource");
            listDrawItemMethod = RequireMethod(
                localGroupType,
                "DrawItem",
                typeof(Rect),
                filterResultType,
                builtinResourceType,
                typeof(bool));

            var projectBrowserType = RequireType("UnityEditor.ProjectBrowser");
            getAllProjectBrowsers = RequireMethod(projectBrowserType, "GetAllProjectBrowsers");
            resetProjectBrowserViews = RequireMethod(projectBrowserType, "ResetViews");
        }

        private static Type RequireType(string fullName)
        {
            return AccessTools.TypeByName(fullName)
                   ?? throw new MissingMemberException($"Required Unity type was not found: {fullName}.");
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] argumentTypes)
        {
            var method = AccessTools.Method(type, name, argumentTypes);
            return method ?? throw new MissingMethodException(type?.FullName, name);
        }

        private static FieldInfo RequireField(Type type, string name)
        {
            return AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
        }

        private static PropertyInfo RequireProperty(Type type, string name)
        {
            return AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);
        }

        private static GUIStyle GetRequiredStyle(FieldInfo field)
        {
            return field.GetValue(null) as GUIStyle
                   ?? throw new InvalidOperationException($"Unity GUI style is not initialized: {field.DeclaringType?.FullName}.{field.Name}.");
        }

        private readonly struct RenameDecisionState
        {
            internal readonly bool RemoveDisplayNameAfterPhysicalRename;
            internal readonly string Guid;
            internal readonly string ExpectedPhysicalName;

            internal RenameDecisionState(string guid, string expectedPhysicalName)
            {
                RemoveDisplayNameAfterPhysicalRename = true;
                Guid = guid;
                ExpectedPhysicalName = expectedPhysicalName;
            }
        }

        private readonly struct TreeDrawState
        {
            private readonly TreeViewItem item;
            private readonly string originalName;
            private readonly StyleColorState lineStyle;
            private readonly StyleColorState boldLineStyle;

            internal TreeDrawState(TreeViewItem item, string originalName, GUIStyle lineStyle, GUIStyle boldLineStyle)
            {
                this.item = item;
                this.originalName = originalName;
                this.lineStyle = new StyleColorState(lineStyle);
                this.boldLineStyle = new StyleColorState(boldLineStyle);
            }

            internal void ApplyColor(Color color)
            {
                lineStyle.Apply(color);
                boldLineStyle.Apply(color);
            }

            internal void Restore()
            {
                if (item != null) item.displayName = originalName;
                lineStyle?.Restore();
                boldLineStyle?.Restore();
            }
        }

        private readonly struct ListDrawState
        {
            private readonly object filterItem;
            private readonly string originalName;
            private readonly StyleColorState listStyle;
            private readonly StyleColorState gridStyle;

            internal ListDrawState(object filterItem, string originalName, GUIStyle listStyle, GUIStyle gridStyle)
            {
                this.filterItem = filterItem;
                this.originalName = originalName;
                this.listStyle = new StyleColorState(listStyle);
                this.gridStyle = new StyleColorState(gridStyle);
            }

            internal void ApplyColor(Color color)
            {
                listStyle.Apply(color);
                gridStyle.Apply(color);
            }

            internal void Restore()
            {
                if (filterItem != null) filterResultNameField.SetValue(filterItem, originalName);
                listStyle?.Restore();
                gridStyle?.Restore();
            }
        }

        private sealed class StyleColorState
        {
            private readonly GUIStyle style;
            private readonly Color normal;
            private readonly Color hover;
            private readonly Color active;
            private readonly Color focused;
            private readonly Color onNormal;
            private readonly Color onHover;
            private readonly Color onActive;
            private readonly Color onFocused;

            internal StyleColorState(GUIStyle style)
            {
                this.style = style;
                normal = style.normal.textColor;
                hover = style.hover.textColor;
                active = style.active.textColor;
                focused = style.focused.textColor;
                onNormal = style.onNormal.textColor;
                onHover = style.onHover.textColor;
                onActive = style.onActive.textColor;
                onFocused = style.onFocused.textColor;
            }

            internal void Apply(Color color)
            {
                style.normal.textColor = color;
                style.hover.textColor = color;
                style.active.textColor = color;
                style.focused.textColor = color;
                style.onNormal.textColor = color;
                style.onHover.textColor = color;
                style.onActive.textColor = color;
                style.onFocused.textColor = color;
            }

            internal void Restore()
            {
                style.normal.textColor = normal;
                style.hover.textColor = hover;
                style.active.textColor = active;
                style.focused.textColor = focused;
                style.onNormal.textColor = onNormal;
                style.onHover.textColor = onHover;
                style.onActive.textColor = onActive;
                style.onFocused.textColor = onFocused;
            }
        }
    }
}
