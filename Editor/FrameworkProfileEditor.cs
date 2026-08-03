using UnityEditor;
using UnityEngine;

namespace ArkFramework.Editor
{
    [CustomEditor(typeof(FrameworkProfile))]
    [CanEditMultipleObjects]
    public sealed class FrameworkProfileEditor : UnityEditor.Editor
    {
        private SerializedProperty _installers;

        private void OnEnable()
        {
            _installers = serializedObject.FindProperty("_installers");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            if (_installers != null)
            {
                EditorGUILayout.PropertyField(
                    _installers,
                    new GUIContent("Module Installers"),
                    includeChildren: true);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The FrameworkProfile installer list could not be found.",
                    MessageType.Error);
            }

            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();

            if (targets.Length == 1)
            {
                DrawValidation(target as FrameworkProfile);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Validation and startup-order preview are available when " +
                    "editing one FrameworkProfile at a time.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Open Debug Window"))
            {
                FrameworkDebugWindow.Open();
            }
        }

        private static void DrawValidation(FrameworkProfile profile)
        {
            if (profile == null)
            {
                EditorGUILayout.HelpBox(
                    "No FrameworkProfile is selected.",
                    MessageType.Info);
                return;
            }

            FrameworkEditorValidationResult result;
            try
            {
                result = FrameworkEditorValidation.Validate(profile);
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (System.Exception exception)
            {
                EditorGUILayout.HelpBox(
                    $"Profile validation failed unexpectedly: " +
                    $"{exception.GetType().Name}: {exception.Message}",
                    MessageType.Error);
                return;
            }

            if (result.IsValid)
            {
                EditorGUILayout.HelpBox(
                    "Profile validation passed.",
                    MessageType.Info);
            }
            else
            {
                for (var index = 0; index < result.Issues.Count; index++)
                {
                    var issue = result.Issues[index];
                    var prefix = issue.InstallerIndex.HasValue
                        ? $"Installer {issue.InstallerIndex.Value}: "
                        : string.Empty;
                    EditorGUILayout.HelpBox(
                        $"{prefix}[{issue.Code}] {issue.Message}",
                        ToMessageType(issue.Severity));
                }
            }

            EditorGUILayout.LabelField("Computed Startup Order", EditorStyles.boldLabel);
            if (result.StartupOrder.Count == 0)
            {
                EditorGUILayout.LabelField(
                    result.IsValid ? "(no modules)" : "(unavailable)");
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                for (var index = 0; index < result.StartupOrder.Count; index++)
                {
                    EditorGUILayout.TextField(
                        $"{index + 1}.",
                        result.StartupOrder[index]);
                }
            }
        }

        private static MessageType ToMessageType(
            FrameworkEditorIssueSeverity severity)
        {
            return severity == FrameworkEditorIssueSeverity.Error
                ? MessageType.Error
                : MessageType.Warning;
        }
    }
}
