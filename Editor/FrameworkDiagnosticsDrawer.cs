using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ArkFramework.Editor
{
    public static class FrameworkDiagnosticsDrawer
    {
        public static string FormatConfigValidation(
            bool? succeeded,
            string error)
        {
            if (!succeeded.HasValue)
            {
                return "尚未校验";
            }

            if (succeeded.Value)
            {
                return "校验通过";
            }

            return string.IsNullOrWhiteSpace(error)
                ? "校验失败"
                : $"校验失败：{error}";
        }

        public static IReadOnlyList<string> FormatFsmTransitions(
            IReadOnlyList<FsmTransitionDiagnostics> transitions)
        {
            if (transitions == null || transitions.Count == 0)
            {
                return Array.AsReadOnly(Array.Empty<string>());
            }

            var snapshot = new List<FsmTransitionDiagnostics>(
                transitions.Count);
            for (var index = 0; index < transitions.Count; index++)
            {
                if (transitions[index] != null)
                {
                    snapshot.Add(transitions[index]);
                }
            }

            snapshot.Sort(CompareTransitions);
            var formatted = new string[snapshot.Count];
            for (var index = 0; index < snapshot.Count; index++)
            {
                var transition = snapshot[index];
                formatted[index] =
                    $"{transition.Trigger} -> {transition.TargetStateId}" +
                    $"（Guard：{(transition.HasGuard ? "有" : "无")}）";
            }

            return Array.AsReadOnly(formatted);
        }

        public static IReadOnlyList<string> SortProcedureTargets(
            IReadOnlyList<string> targetProcedureIds)
        {
            if (targetProcedureIds == null ||
                targetProcedureIds.Count == 0)
            {
                return Array.AsReadOnly(Array.Empty<string>());
            }

            var snapshot = new List<string>(targetProcedureIds.Count);
            for (var index = 0; index < targetProcedureIds.Count; index++)
            {
                var target = targetProcedureIds[index];
                if (!string.IsNullOrWhiteSpace(target))
                {
                    snapshot.Add(target);
                }
            }

            snapshot.Sort(StringComparer.Ordinal);
            return Array.AsReadOnly(snapshot.ToArray());
        }

        internal static void DrawDuration(string label, TimeSpan duration)
        {
            DrawValue(
                $"{label} Duration",
                $"{duration.TotalMilliseconds:F3} ms");
        }

        internal static void DrawException(
            string label,
            Exception exception)
        {
            if (exception == null)
            {
                DrawValue(label, "(none)");
                return;
            }

            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                FormatException(exception),
                EditorStyles.textArea,
                GUILayout.MinHeight(36f));
        }

        internal static void DrawValue(string label, object value)
        {
            EditorGUILayout.LabelField(
                label,
                value?.ToString() ?? "(null)");
        }

        private static int CompareTransitions(
            FsmTransitionDiagnostics left,
            FsmTransitionDiagnostics right)
        {
            var result = string.Compare(
                left.Trigger,
                right.Trigger,
                StringComparison.Ordinal);
            if (result != 0)
            {
                return result;
            }

            result = string.Compare(
                left.TargetStateId,
                right.TargetStateId,
                StringComparison.Ordinal);
            return result != 0
                ? result
                : left.HasGuard.CompareTo(right.HasGuard);
        }

        private static string FormatException(Exception exception)
        {
            try
            {
                return exception.ToString();
            }
            catch
            {
                // 诊断异常的格式化失败时，仍应保留最低限度的类型信息。
                return exception.GetType().FullName ?? exception.GetType().Name;
            }
        }
    }
}
