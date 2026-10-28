using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Foldout section drawing shared by the settings Inspector. Sections remember
    /// whether they were open, and a collapsed one still shows its state in a summary
    /// on the right, so closing a section never hides what it is set to.
    /// </summary>
    public static class InspectorSections
    {
        /// <summary>
        /// Draws a section header. Returns true when the body should be drawn.
        /// </summary>
        /// <param name="key">Stable key for remembering the open state.</param>
        /// <param name="label">Section title.</param>
        /// <param name="summary">
        /// Short state description shown on the right, e.g. "on · 50k". Keep it to a few
        /// characters: it is there to be glanced at, not read.
        /// </param>
        /// <param name="defaultOpen">Open state the first time this section is seen.</param>
        public static bool Foldout(string key, string label, string summary = null, bool defaultOpen = false)
        {
            string prefKey = $"CustomTools.TerrainGeneration.section.{key}";
            bool open = EditorPrefs.GetBool(prefKey, defaultOpen);

            var style = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontStyle = FontStyle.Bold
            };

            var rect = GUILayoutUtility.GetRect(1f, EditorGUIUtility.singleLineHeight + 6f);

            // Draw the summary first so the foldout's own hit area stays on top;
            // otherwise clicking near the summary misses the toggle.
            if (!string.IsNullOrEmpty(summary))
            {
                var summaryRect = rect;
                summaryRect.xMin = rect.xMax - 160f;
                summaryRect.xMax = rect.xMax - 6f;

                var summaryStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
                };
                GUI.Label(summaryRect, summary, summaryStyle);
            }

            bool newOpen = EditorGUI.BeginFoldoutHeaderGroup(rect, open, label, style);
            EditorGUI.EndFoldoutHeaderGroup();

            if (newOpen != open) EditorPrefs.SetBool(prefKey, newOpen);

            return newOpen;
        }

        /// <summary>
        /// Draws a nested "Advanced" disclosure for the dials that only matter during a
        /// tuning session. Returns true when its body should be drawn.
        /// </summary>
        public static bool Advanced(string key, string hint = null)
        {
            string prefKey = $"CustomTools.TerrainGeneration.advanced.{key}";
            bool open = EditorPrefs.GetBool(prefKey, false);

            string label = string.IsNullOrEmpty(hint) || open ? "Advanced" : $"Advanced  ({hint})";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f);
                bool newOpen = EditorGUILayout.Foldout(open, label, true, EditorStyles.miniBoldLabel);
                if (newOpen != open) EditorPrefs.SetBool(prefKey, newOpen);
                open = newOpen;
            }

            return open;
        }

        /// <summary>
        /// A slider with a plainer label than the field name, plus a caption under it
        /// saying what the two ends mean. Abstract numbers are the hardest part of this
        /// Inspector to read, and a direction hint costs one line.
        /// </summary>
        public static void SliderWithHint(
            SerializedProperty property, string label, string lowHint, string highHint)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label));

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
            };

            var rect = EditorGUILayout.GetControlRect(false, 13f);
            rect.xMin += EditorGUIUtility.labelWidth + 2f;
            GUI.Label(rect, $"{lowHint}  ←→  {highHint}", style);
        }
    }
}
